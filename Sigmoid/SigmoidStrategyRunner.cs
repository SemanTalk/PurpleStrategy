using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CryptoFuturesBacktester.Core.Contracts;
using CryptoFuturesBacktester.Core.Enums;
using CryptoFuturesBacktester.Core.Models;
using PurpleStrategy.Models;
using PurpleStrategy.Utils;

namespace PurpleStrategy.Sigmoid;

/// <summary>
/// SigmoidWeightStrategy 본체에서 추출한 per-bar 실행 파이프라인.
/// IStrategy 구현체와 Logic Canvas emit 양쪽이 동일 인스턴스로 wrap → 동작 lockstep.
/// 외부 청산 감지, equity-lag 보정, ATR-SL/TP 까지 포함된 자기완결 헬퍼.
/// </summary>
public sealed class SigmoidStrategyRunner
{
    private SigmoidPositionManager? _manager;
    private IStrategyContext? _ctx;

    // ── LTF caches
    private PlotCache? _ltfBB, _ltfPurple, _ltfEma, _ltfVol;
    private IReadOnlyList<PurpleMetrics>? _ltfMetricsList;
    private Dictionary<DateTime, PurpleMetrics>? _ltfMap;

    // ── HTF caches
    private PlotCache? _htfBB, _htfPurple, _htfEma, _htfVol;
    private IReadOnlyList<PurpleMetrics>? _htfMetricsList;
    private Dictionary<DateTime, PurpleMetrics>? _htfMap;
    private int _htfInterval = 60;

    // ── Trading state
    private PositionSide? _activeSide;
    private decimal _prevEquity;

    // === LTF Probe (Plan B 진단용 — 분석 후 블록 단위 제거) ==============
    // [2026-05-14] LR axis swap — htf_lr_slope (Slope/Close, 정규화) + htf_lr_r2 추가.
    private record LtfProbeSnap(
        DateTime EntryDate, PositionSide Side, decimal EntryPrice, decimal EntryQty,
        double LtfBBW, double LtfEmaSpread, double LtfVolRatio, int LtfTrendSign,
        double HtfTrend, int HtfTrendSign, double HtfLrSlope, double HtfLrR2, double Strength);
    private LtfProbeSnap? _probeSnap;
    private string? _probePath;
    private bool _probeInit;
    // ====================================================================

    // ── Config
    public decimal Leverage { get; set; } = 10m;
    public decimal SlMultiplier { get; set; }
    public decimal TpMultiplier { get; set; }

    // ── Read-only accessors (GetBarLog / GetChartOverlays 가 참조)
    public SigmoidPositionManager? Manager => _manager;
    public SigmoidPositionManager.OrderInstruction? LastInstr { get; private set; }
    public PurpleMetrics? LastMetrics { get; private set; }
    public PurpleMetrics? LastHtfMetrics { get; private set; }
    public BarContext? LastBarCtx { get; private set; }
    public string? LastDebugMsg { get; private set; }
    public string? LastHtfDebugMsg { get; private set; }
    public PositionSide? ActiveSide => _activeSide;
    public PlotCache? LtfBollingerBands => _ltfBB;
    public PlotCache? LtfDualEma => _ltfEma;
    public PlotCache? LtfPurple => _ltfPurple;
    public PlotCache? LtfVol => _ltfVol;

    /// <summary>
    /// Indicator caches + manager 를 (재)구성한다. _manager 는 ??= 로 보호되어 weight/peak 상태가 유지된다.
    /// 라이브 ForwardTest 가 매 바 호출하더라도 manager state 는 안전.
    /// </summary>
    public void Initialize(IStrategyContext ctx, SigmoidParams paramsRecord)
    {
        _ctx = ctx;
        _htfInterval = ParseIntervalMinutes(ctx.Config.HtfInterval);
        _manager ??= new SigmoidPositionManager { Params = paramsRecord };

        var freshBB = SkenderUtils.RenderBollingerBands(_ctx.QuoteHistory);
        var freshPurple = SkenderUtils.RenderPurpleIndicator(_ctx.QuoteHistory);
        var freshEMA = SkenderUtils.RenderDualEma(_ctx.QuoteHistory);
        var freshVol = SkenderUtils.RenderVolume(_ctx.QuoteHistory);

        if (freshBB is not null) _ltfBB = freshBB;
        if (freshPurple is not null) _ltfPurple = freshPurple;
        if (freshEMA is not null) _ltfEma = freshEMA;
        if (freshVol is not null) _ltfVol = freshVol;

        if (freshBB?.mBollingerBandsResults == null) return;
        if (freshEMA?.mShortEmaResults == null) return;
        if (freshVol?.mVolEmaResults == null) return;
        if (freshPurple?.mSPbResults == null) return;
        if (_ctx.QuoteHistory.Count < 4) return;

        var ltfLookup = PurpleMetricsUtils.IndicatorLookup.BuildIndicatorLookup(
            _ltfBB!, _ltfEma!, _ltfVol!, _ltfPurple!);
        _ltfMetricsList = PurpleMetricsUtils.IndicatorLookup.BuildMetricsList(
            _ctx.QuoteHistory, ltfLookup, SkenderUtils.mLtfLRgressionLookback);
        if (_ltfMetricsList is not null)
        {
            _ltfMap = new Dictionary<DateTime, PurpleMetrics>(_ltfMetricsList.Count);
            foreach (var m in _ltfMetricsList) _ltfMap[m.Date] = m;
        }

        _htfBB = SkenderUtils.RenderBollingerBands(_ctx.HtfQuoteHistory);
        _htfPurple = SkenderUtils.RenderPurpleIndicator(_ctx.HtfQuoteHistory);
        _htfEma = SkenderUtils.RenderDualEma(_ctx.HtfQuoteHistory);
        _htfVol = SkenderUtils.RenderVolume(_ctx.HtfQuoteHistory);

        if (_htfBB?.mBollingerBandsResults == null) return;
        if (_htfEma?.mShortEmaResults == null) return;
        if (_htfVol?.mVolEmaResults == null) return;
        if (_htfPurple?.mSPbResults == null) return;
        if (_ctx.HtfQuoteHistory.Count < 4) return;

        var htfLookup = PurpleMetricsUtils.IndicatorLookup.BuildIndicatorLookup(
            _htfBB, _htfEma, _htfVol, _htfPurple);
        _htfMetricsList = PurpleMetricsUtils.IndicatorLookup.BuildMetricsList(
            _ctx.HtfQuoteHistory, htfLookup, SkenderUtils.mHtfLRgressionLookback);
        if (_htfMetricsList is not null)
        {
            _htfMap = new Dictionary<DateTime, PurpleMetrics>(_htfMetricsList.Count);
            foreach (var m in _htfMetricsList) _htfMap[m.Date] = m;
        }
    }

    /// <summary>
    /// 하나의 확정 봉을 처리한다. 반환값은 진단/로그 용 OrderInstruction (skip 케이스에서는 null).
    /// </summary>
    /// <param name="bar">엔진이 넘긴 확정 봉 컨텍스트.</param>
    /// <param name="signalGate">
    /// Canvas 상류 signal port 값.
    /// true  → 평소처럼 manager.OnBarClose() + ExecuteInstruction() 둘 다 수행.
    /// false → 신규 진입/포지션 변경을 일체 차단 (manager state 도 advance 시키지 않음).
    /// </param>
    public SigmoidPositionManager.OrderInstruction? OnBar(BarContext bar, bool signalGate = true)
    {
        if (_ctx is null || _manager is null) return null;

        LastInstr = null;
        LastBarCtx = null;
        LastDebugMsg = null;
        LastHtfMetrics = null;
        LastHtfDebugMsg = null;

        var i = bar.BarIndex;
        var quote = bar.Quote;
        var price = quote.Close;
        var symbol = quote.Symbol;

        PurpleMetrics? htfMetric = null;
        if (bar.HtfHistory.Count > 0 && _htfMap is not null)
            _htfMap.TryGetValue(bar.HtfHistory[^1].Date, out htfMetric);
        LastHtfMetrics = htfMetric;

        // 엔진 자동 청산 (SL/TP/청산가/MaxDD) 후 manager 동기화
        if (_activeSide.HasValue && !_ctx.Positions.HasPosition(symbol, _activeSide.Value))
        {
            ProbeOnClose(bar, price);
            _manager.SyncPosition("None", 0);
            _activeSide = null;
        }

        if (i == 0 || _ltfMetricsList is null)
        {
            LastDebugMsg = i == 0
                ? null
                : $"[Skip #{i}] _ltfMetricsList null — Initialize() 지표 캐시 실패";
            _prevEquity = bar.AccountEquity;
            foreach (var testSide in new[] { PositionSide.Long, PositionSide.Short })
            {
                var existing = _ctx.Positions.GetPosition(symbol, testSide);
                if (existing is null) continue;
                _activeSide = testSide;
                var weight = (double)(existing.Quantity * price / (bar.AccountEquity * Leverage));
                _manager.SyncPosition(testSide == PositionSide.Long ? "Long" : "Short", weight);
                break;
            }
            return null;
        }

        if (_ltfMap is null || !_ltfMap.TryGetValue(quote.Date, out var ltfMetric))
        {
            LastDebugMsg = $"[Skip #{i}] _ltfMap miss bar={quote.Date:HH:mm:ss}";
            _prevEquity = bar.AccountEquity;
            return null;
        }

        LastHtfDebugMsg = BuildHtfDebugMessage(bar, ltfMetric, htfMetric);

        // signalGate=false → 진단 캐시만 남기고 manager/주문 모두 건너뜀.
        // Canvas 상류 AI/TimeFilter/Condition 이 0/false 일 때 dispatcher 가 no-op 가 되도록.
        if (!signalGate)
        {
            LastBarCtx = bar;
            LastMetrics = ltfMetric;
            LastDebugMsg = $"[Gate OFF #{i}] signalGate=false — 진입/리밸런싱 차단";
            _prevEquity = bar.AccountEquity;
            return null;
        }

        var equity = _prevEquity > 0 ? _prevEquity : bar.AccountEquity;
        var instr = _manager.OnBarClose(ltfMetric, htfMetric, equity, (double)Leverage);

        LastBarCtx = bar;
        LastInstr = instr;
        LastMetrics = ltfMetric;

        var sideBeforeExec = _activeSide;
        ExecuteInstruction(symbol, price, equity, instr);

        // === LTF Probe — manager-driven state transitions ===
        if (sideBeforeExec is null && _activeSide.HasValue)
            ProbeOnEntry(bar, ltfMetric, htfMetric, instr, _activeSide.Value, symbol);
        else if (sideBeforeExec.HasValue && _activeSide is null)
            ProbeOnClose(bar, price);

        _prevEquity = bar.AccountEquity;
        return instr;
    }

    // ── 주문 실행 ────────────────────────────────────────────────────

    private void ExecuteInstruction(
        string symbol, decimal price, decimal equity,
        SigmoidPositionManager.OrderInstruction instr)
    {
        switch (instr.Action)
        {
            case SigmoidPositionManager.OrderAction.Hold:
                if (_activeSide.HasValue && instr.CurrentWeight > 0.001 && instr.Side != "None")
                    ApplyWeight(symbol, price, equity, instr.CurrentWeight, instr.Side, OrderType.Limit);
                break;

            case SigmoidPositionManager.OrderAction.CloseAll:
                CloseActive(symbol, price);
                break;

            case SigmoidPositionManager.OrderAction.IncreasePosition:
                ApplyWeight(symbol, price, equity, instr.CurrentWeight, instr.Side, OrderType.Limit);
                break;

            case SigmoidPositionManager.OrderAction.DecreasePosition:
                if (_activeSide.HasValue)
                    ApplyWeight(symbol, price, equity, instr.CurrentWeight, instr.Side, OrderType.Limit);
                break;
        }
    }

    private void ApplyWeight(
        string symbol, decimal price, decimal equity,
        double weight, string side, OrderType orderType = OrderType.Market)
    {
        if (_ctx is null) return;
        if (weight < 0.001 || side == "None") { CloseActive(symbol, price); return; }

        var ps = side == "Long" ? PositionSide.Long : PositionSide.Short;
        decimal targetNotional = equity * Leverage * (decimal)weight;
        decimal targetQty = targetNotional / price;
        if (targetQty <= 0) return;

        var pos = _ctx.Positions.GetPosition(symbol, ps);
        if (pos is null)
        {
            var opened = _ctx.Positions.OpenPosition(symbol, ps, targetQty, Leverage, price, orderType);
            _activeSide = ps;
            if (opened is not null) ApplySLTP(opened);
        }
        else
        {
            decimal deltaQty = targetQty - pos.Quantity;
            if (deltaQty > 0.001m)
            {
                var updated = _ctx.Positions.IncreasePosition(symbol, ps, deltaQty, price, orderType);
                if (updated is not null) ApplySLTP(updated);
            }
            else if (deltaQty < -0.001m)
            {
                decimal reduceQty = -deltaQty;
                _ctx.Positions.DecreasePosition(symbol, ps, reduceQty, price, CloseReason.Decrease, orderType);
                if (!_ctx.Positions.HasPosition(symbol, ps)) _activeSide = null;
            }
        }
    }

    private void CloseActive(string symbol, decimal price)
    {
        if (_ctx is null) return;
        if (_activeSide.HasValue && _ctx.Positions.HasPosition(symbol, _activeSide.Value))
            _ctx.Positions.CloseSide(symbol, _activeSide.Value, price);
        _activeSide = null;
    }

    private void ApplySLTP(FuturesPosition pos)
    {
        if (_ctx is null || LastMetrics is null) return;
        var atr = (decimal)LastMetrics.ATR;
        var entry = pos.EntryPrice;

        if (pos.Side == PositionSide.Long)
        {
            if (SlMultiplier > 0) { var sl = entry - atr * SlMultiplier; if (sl > 0) _ctx.Positions.SetStopLoss(pos.Id, sl); }
            if (TpMultiplier > 0) { var tp = entry + atr * TpMultiplier; if (tp > 0) _ctx.Positions.SetTakeProfit(pos.Id, tp); }
        }
        else
        {
            if (SlMultiplier > 0) { var sl = entry + atr * SlMultiplier; if (sl > 0) _ctx.Positions.SetStopLoss(pos.Id, sl); }
            if (TpMultiplier > 0) { var tp = entry - atr * TpMultiplier; if (tp > 0) _ctx.Positions.SetTakeProfit(pos.Id, tp); }
        }
    }

    private string BuildHtfDebugMessage(BarContext bar, PurpleMetrics ltfM, PurpleMetrics? htfMetric)
    {
        if (_ctx?.Config.HtfInterval is null)
            return $"  HTF  cfg:None  hist:{bar.HtfHistory.Count}  map:{_htfMap?.Count ?? 0}  metric:{FormatDate(htfMetric?.Date)}";

        var currentBucket = PurpleMetricsUtils.IndicatorLookup.GetBucketKey(ltfM.Date, _htfInterval);
        var expectedKey = currentBucket.AddMinutes(-_htfInterval);
        DateTime? engineLastHtf = bar.HtfHistory.Count > 0 ? bar.HtfHistory[^1].Date : null;
        DateTime? metricDate = htfMetric?.Date;

        var aligned =
            !engineLastHtf.HasValue && !metricDate.HasValue ? "NONE" :
            engineLastHtf.HasValue && metricDate.HasValue && engineLastHtf.Value == metricDate.Value ? "OK" :
            "CHECK";

        return $"  HTF  cfg:{_ctx.Config.HtfInterval}  interval:{_htfInterval}m  hist:{bar.HtfHistory.Count}  map:{_htfMap?.Count ?? 0}  " +
               $"engineLast:{FormatDate(engineLastHtf)}  key:{expectedKey:MM-dd HH:mm}  " +
               $"metric:{FormatDate(metricDate)}  align:{aligned}";
    }

    private static string FormatDate(DateTime? date) =>
        date.HasValue ? date.Value.ToString("MM-dd HH:mm") : "null";

    private static int ParseIntervalMinutes(string? interval) => interval switch
    {
        "D" => 1440,
        "W" => 10080,
        "M" => 43200,
        _ => int.TryParse(interval, out var minutes) && minutes > 0 ? minutes : 60,
    };

    // === LTF Probe helpers (Plan B 진단용 — 분석 후 블록 단위 제거) ======

    private void EnsureProbe()
    {
        if (_probeInit) return;
        _probeInit = true;
        var dir = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(dir);
        _probePath = Path.Combine(dir, $"ltf_probe_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(_probePath,
            "entry_date,close_date,side,entry_price,close_price,realized_pnl," +
            "ltf_bbw,ltf_ema_spread,ltf_vol_ratio,ltf_trend_sign," +
            "htf_trend,htf_trend_sign,htf_lr_slope,htf_lr_r2,strength\n");
    }

    private void ProbeOnEntry(
        BarContext bar, PurpleMetrics ltf, PurpleMetrics? htf,
        SigmoidPositionManager.OrderInstruction instr, PositionSide side, string symbol)
    {
        EnsureProbe();
        var pos = _ctx?.Positions.GetPosition(symbol, side);
        if (pos is null) return;
        double emaSpread = ltf.Close != 0 ? (ltf.ShortEma - ltf.LongEma) / (double)ltf.Close : 0;
        double volRatio = ltf.VolEma > 0 ? (double)ltf.Volume / ltf.VolEma : 0;
        double htfLrSlope = (htf is not null && htf.Close > 0)
            ? htf.LRResult.Slope / (double)htf.Close : 0;
        double htfLrR2 = htf?.LRResult.RSquared ?? 0;
        _probeSnap = new LtfProbeSnap(
            bar.Quote.Date, side, pos.EntryPrice, pos.Quantity,
            ltf.BBW, emaSpread, volRatio, Math.Sign(ltf.Trend),
            htf?.Trend ?? 0, htf is null ? 0 : Math.Sign(htf.Trend),
            htfLrSlope, htfLrR2,
            instr.Strength);
    }

    private void ProbeOnClose(BarContext bar, decimal closePrice)
    {
        if (_probeSnap is null || _probePath is null) return;
        var s = _probeSnap;
        var sideMul = s.Side == PositionSide.Long ? 1m : -1m;
        var realized = (closePrice - s.EntryPrice) * s.EntryQty * sideMul;
        var inv = CultureInfo.InvariantCulture;
        File.AppendAllText(_probePath, string.Join(",",
            s.EntryDate.ToString("yyyy-MM-dd HH:mm:ss", inv),
            bar.Quote.Date.ToString("yyyy-MM-dd HH:mm:ss", inv),
            s.Side,
            s.EntryPrice.ToString(inv),
            closePrice.ToString(inv),
            realized.ToString("F4", inv),
            s.LtfBBW.ToString("F4", inv),
            s.LtfEmaSpread.ToString("F6", inv),
            s.LtfVolRatio.ToString("F4", inv),
            s.LtfTrendSign.ToString(inv),
            s.HtfTrend.ToString("F6", inv),
            s.HtfTrendSign.ToString(inv),
            s.HtfLrSlope.ToString("F8", inv),
            s.HtfLrR2.ToString("F4", inv),
            s.Strength.ToString("F4", inv)) + "\n");
        _probeSnap = null;
    }
    // ====================================================================
}
