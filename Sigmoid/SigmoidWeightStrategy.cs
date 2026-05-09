using CryptoFuturesBacktester.Core.Contracts;
using CryptoFuturesBacktester.Core.Enums;
using CryptoFuturesBacktester.Core.Models;
using PurpleStrategy.Models;
using PurpleStrategy.Sigmoid;
using PurpleStrategy.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UserStrategy;

public sealed class SigmoidWeightStrategy : IParametrizedStrategy, IChartableStrategy, ILoggableStrategy, ILiveBarRefreshStrategy
{
    // ── SigmoidPositionManager
    private SigmoidPositionManager _manager = default!;

    private decimal _leverage = 10m;

    // ── 지표 캐시

    private IStrategyContext _ctx = default!;
    private PlotCache? _ltfPurpleIndicatorPlotCache = null;
    private PlotCache? _ltfBollingerBandsPlotCache = null;
    private PlotCache? _ltfDualEmaPlotCache = null;
    private PlotCache? _ltfVolPlotCache = null;
    private IReadOnlyList<PurpleMetrics>? _ltfMetricsList = null;
    private Dictionary<DateTime, PurpleMetrics>? _ltfMap = null;
    private PlotCache? _htfPurpleIndicatorPlotCache = null;
    private PlotCache? _htfBollingerBandsPlotCache = null;
    private PlotCache? _htfDualEmaPlotCache = null;
    private PlotCache? _htfVolPlotCache = null;
    private IReadOnlyList<PurpleMetrics>? _htfMetricsList = null;
    private int _htfInterval = 60;
    private Dictionary<DateTime, PurpleMetrics>? _htfMap = null;

    // ── 외부 청산 추적

    private PositionSide? _activeSide = null;

    private decimal _prevEquity = 0m;

    // ── ILoggableStrategy 캐시 — GetBarLog()에서 참조

    private SigmoidPositionManager.OrderInstruction? _lastInstr;
    private PurpleMetrics? _lastMetrics;
    private PurpleMetrics? _lastHtfMetrics;
    private BarContext? _lastBarCtx;
    private string? _lastDebugMsg; // 조기 반환 시 원인 진단 메시지
    private string? _lastHtfDebugMsg;

    // ── 파라미터 저장 필드 (Initialize 전에 SetParameters 호출됨) ─────
    private decimal _pLeverage = 10m;

    //SlMultiplier = 0 또는 TpMultiplier = 0 으로 개별 비활성화 가능
    private decimal _pSlMultiplier = 0;

    private decimal _pTpMultiplier = 0m;

    private decimal _pConfirmFullScale = 15m;
    private decimal _pHeatFullScale = 20m;
    private decimal _pHeatPenalty = 0.1m;
    private decimal _pSteepness = 10m;
    private decimal _pMomentumBoost = 0.1m;
    private decimal _pEnergyBoost = 0.2m;
    private decimal _pMinTrendMagnitude = 0.01m;
    private decimal _pBBWMinThreshold = 18m;
    private decimal _pMddDefenseThreshold = 0.15m;
    private decimal _pMddDefenseMaxWeight = 0.10m;
    private decimal _pMddDefenseExitThreshold = 0.05m;
    private decimal _pReversalBlockThreshold = 0.4m;
    private decimal _pReversalSignalThreshold = 0.6m;
    private decimal _pHtfReversalThreshold = 0.6m;
    private decimal _pMaxWeightChangePerBar = 0.003m;
    private decimal _pExitSpeedMultiplier = 3m;
    private decimal _pMaxWeight = 0.3m;

    public string Name =>
         $"Sigmoid  Lev={_leverage}x  MaxW={_pMaxWeight:P0}";

    // ── IParametrizedStrategy ─────────────────────────────────────────

    public IReadOnlyList<StrategyParameter> GetParameters() =>
    [
        //신호 파라미터

        new("ConfirmFullScale","ConfirmFullScale",       StrategyParamType.Decimal,  _pConfirmFullScale,  1m, 25m, 5m, 0),
        new("HeatFullScale","HeatFullScale",       StrategyParamType.Decimal,  _pHeatFullScale,  1m, 35m,   5m, 0),
        new("HeatPenalty","HeatPenalty",       StrategyParamType.Decimal,  _pHeatPenalty,  0.0m, 1.5m,   0.5m, 1),
        new("Steepness","Steepness",       StrategyParamType.Decimal,  _pSteepness,  0m, 15m,   5.0m, 1),
        new("MomentumBoost","MomentumBoost",       StrategyParamType.Decimal,  _pMomentumBoost,  0.0m, 0.5m,   0.2m, 1),
        new("EnergyBoost","EnergyBoost",       StrategyParamType.Decimal,  _pEnergyBoost,  0.0m, 1.5m,   0.5m, 1),
        new("MinTrendMagnitude",    "MinTrendMagnitude",     StrategyParamType.Decimal,  _pMinTrendMagnitude,  0.0m,  0.20m,   0.05m, 2),
        new("BBWMinThreshold",    "BBWMinThreshold",     StrategyParamType.Decimal,  _pBBWMinThreshold,  15m,  20m,   1m, 0),

        //포지션 파라미터
        new("Leverage",    "Leverage",           StrategyParamType.Decimal,  _pLeverage,  1m, 10m, 2m, 0),
        new("MddDefenseThreshold", "MddDefenseThreshold", StrategyParamType.Decimal, _pMddDefenseThreshold, 0.0m, 1.0m, 0.5m, 1),
        new("MddDefenseMaxWeight", "MddDefenseMaxWeight", StrategyParamType.Decimal, _pMddDefenseMaxWeight, 0.0m, 1.0m, 0.5m, 1),
        new("MddDefenseExitThreshold", "MddDefenseExitThreshold", StrategyParamType.Decimal, _pMddDefenseExitThreshold, 0m, 20m, 5m, 1),
        new("ReversalBlockThreshold", "ReversalBlockThreshold", StrategyParamType.Decimal, _pReversalBlockThreshold, 0.0m, 1.0m, 0.5m, 1),
        new("ReversalSignalThreshold", "ReversalSignalThreshold", StrategyParamType.Decimal, _pReversalSignalThreshold, 0.0m, 1.0m, 0.5m, 1),
        new("HtfReversalThreshold", "HtfReversalThreshold", StrategyParamType.Decimal, _pHtfReversalThreshold, 0.0m, 1.0m, 0.5m, 1),
        new("MaxWeightChangePerBar","MaxWeightChangePerBar",       StrategyParamType.Decimal,  _pMaxWeightChangePerBar,  0.001m, 0.010m, 0.002m, 3),
        new("ExitSpeedMultiplier","ExitSpeedMultiplier",       StrategyParamType.Decimal,  _pExitSpeedMultiplier,  1m, 100m, 10m, 0),
        new("MaxWeight",   "MaxWeight",        StrategyParamType.Decimal, _pMaxWeight, 0.1m, 0.5m,  0.1m, 1),
        new("SlMultiplier","SL Multiplier (ATR×)", StrategyParamType.Decimal, _pSlMultiplier, 0m, 5m, 1m, 0),
        new("TpMultiplier","TP Multiplier (ATR×)", StrategyParamType.Decimal, _pTpMultiplier, 0m, 10m, 2m, 0),
    ];

    public void SetParameters(IReadOnlyDictionary<string, decimal> values)
    {
        if (values.TryGetValue("Leverage", out var v)) _pLeverage = v;
        if (values.TryGetValue("ConfirmFullScale", out v)) _pConfirmFullScale = v;
        if (values.TryGetValue("HeatFullScale", out v)) _pHeatFullScale = v;
        if (values.TryGetValue("HeatPenalty", out v)) _pHeatPenalty = v;
        if (values.TryGetValue("Steepness", out v)) _pSteepness = v;
        if (values.TryGetValue("MomentumBoost", out v)) _pMomentumBoost = v;
        if (values.TryGetValue("EnergyBoost", out v)) _pEnergyBoost = v;
        if (values.TryGetValue("MinTrendMagnitude", out v)) _pMinTrendMagnitude = v;
        if (values.TryGetValue("MaxWeightChangePerBar", out v)) _pMaxWeightChangePerBar = v;
        if (values.TryGetValue("BBWMinThreshold", out v)) _pBBWMinThreshold = v;

        if (values.TryGetValue("MddDefenseThreshold", out v)) _pMddDefenseThreshold = v;
        if (values.TryGetValue("MddDefenseMaxWeight", out v)) _pMddDefenseMaxWeight = v;
        if (values.TryGetValue("MddDefenseExitThreshold", out v)) _pMddDefenseExitThreshold = v;
        if (values.TryGetValue("ReversalBlockThreshold", out v)) _pReversalBlockThreshold = v;
        if (values.TryGetValue("ReversalSignalThreshold", out v)) _pReversalSignalThreshold = v;
        if (values.TryGetValue("HtfReversalThreshold", out v)) _pHtfReversalThreshold = v;

        if (values.TryGetValue("MaxWeight", out v)) _pMaxWeight = v;
        if (values.TryGetValue("SlMultiplier", out v)) _pSlMultiplier = v;
        if (values.TryGetValue("TpMultiplier", out v)) _pTpMultiplier = v;
    }

    public void Initialize(IStrategyContext context)
    {
        _ctx = context;
        _leverage = _pLeverage;
        _htfInterval = ParseIntervalMinutes(_ctx.Config.HtfInterval);

        // [2026-05-05 20:18 +09:00] Initialize는 최초 1회 실행된다.
        // 라이브 새 봉에서 자체 지표 맵만 갱신할 때도 _manager 상태가 유지되도록 ??= 로 보호한다.
        _manager ??= new SigmoidPositionManager
        {
            Params = new SigmoidParams
            {
                ConfirmFullScale = (double)_pConfirmFullScale,
                HeatFullScale = (double)_pHeatFullScale,
                HeatPenalty = (double)_pHeatPenalty,
                Steepness = (double)_pSteepness,
                MomentumBoost = (double)_pMomentumBoost,
                EnergyBoost = (double)_pEnergyBoost,
                MinTrendMagnitude = (double)_pMinTrendMagnitude,
                MaxWeightChangePerBar = (double)_pMaxWeightChangePerBar,
                BBWMinThreshold = (double)_pBBWMinThreshold,
                MddDefenseThreshold = (double)_pMddDefenseThreshold,
                MddDefenseMaxWeight = (double)_pMddDefenseMaxWeight,
                MddDefenseExitThreshold = (double)_pMddDefenseExitThreshold,
                ReversalBlockThreshold = (double)_pReversalBlockThreshold,
                ReversalSignalThreshold = (double)_pReversalSignalThreshold,
                HtfReversalThreshold = (double)_pHtfReversalThreshold,

                MaxWeight = (double)_pMaxWeight,
            }
        };

        //LTF — 렌더 성공 시에만 캐시 갱신 (실패해도 이전 캐시 보존 → 차트 오버레이 지속 표시)
        var freshBB = SkenderUtils.RenderBollingerBands(_ctx.QuoteHistory);
        var freshPurple = SkenderUtils.RenderPurpleIndicator(_ctx.QuoteHistory);
        var freshEMA = SkenderUtils.RenderDualEma(_ctx.QuoteHistory);
        var freshVol = SkenderUtils.RenderVolume(_ctx.QuoteHistory);

        if (freshBB is not null) _ltfBollingerBandsPlotCache = freshBB;
        if (freshPurple is not null) _ltfPurpleIndicatorPlotCache = freshPurple;
        if (freshEMA is not null) _ltfDualEmaPlotCache = freshEMA;
        if (freshVol is not null) _ltfVolPlotCache = freshVol;

        //PurpleMetrics List — 트레이딩 로직은 fresh 결과가 유효할 때만 갱신
        if (freshBB?.mBollingerBandsResults == null) return;
        if (freshEMA?.mShortEmaResults == null) return;
        if (freshVol?.mVolEmaResults == null) return;
        if (freshPurple?.mSPbResults == null) return;
        if (_ctx.QuoteHistory.Count < 4) return;

        var ltfLookup = PurpleMetricsUtils.IndicatorLookup.BuildIndicatorLookup(_ltfBollingerBandsPlotCache!, _ltfDualEmaPlotCache!, _ltfVolPlotCache!, _ltfPurpleIndicatorPlotCache!);
        _ltfMetricsList = PurpleMetricsUtils.IndicatorLookup.BuildMetricsList(_ctx.QuoteHistory, ltfLookup, SkenderUtils.mRegressionLookback);
        if (_ltfMetricsList is not null)
        {
            // ToDictionary 대신 last-write-wins — REST 워밍업 데이터에 중복 타임스탬프가 있어도 안전
            _ltfMap = new Dictionary<DateTime, PurpleMetrics>(_ltfMetricsList.Count);
            foreach (var m in _ltfMetricsList) _ltfMap[m.Date] = m;
        }

        //HTF
        //볼린저밴드
        _htfBollingerBandsPlotCache = SkenderUtils.RenderBollingerBands(_ctx.HtfQuoteHistory);
        //퍼플지표
        _htfPurpleIndicatorPlotCache = SkenderUtils.RenderPurpleIndicator(_ctx.HtfQuoteHistory);
        //듀얼 지수이동평균
        _htfDualEmaPlotCache = SkenderUtils.RenderDualEma(_ctx.HtfQuoteHistory);
        //거래량
        _htfVolPlotCache = SkenderUtils.RenderVolume(_ctx.HtfQuoteHistory);

        //PurpleMetrics List
        if (_htfBollingerBandsPlotCache?.mBollingerBandsResults == null) return;
        if (_htfDualEmaPlotCache?.mShortEmaResults == null) return;
        if (_htfVolPlotCache?.mVolEmaResults == null) return;
        if (_htfPurpleIndicatorPlotCache?.mSPbResults == null) return;
        if (_ctx.HtfQuoteHistory.Count < 4) return;

        var htfLookup = PurpleMetricsUtils.IndicatorLookup.BuildIndicatorLookup(_htfBollingerBandsPlotCache, _htfDualEmaPlotCache, _htfVolPlotCache, _htfPurpleIndicatorPlotCache);
        _htfMetricsList = PurpleMetricsUtils.IndicatorLookup.BuildMetricsList(_ctx.HtfQuoteHistory, htfLookup, SkenderUtils.mRegressionLookback);
        if (_htfMetricsList is not null)
        {
            // ToDictionary 대신 last-write-wins — REST 워밍업 데이터에 중복 타임스탬프가 있어도 안전
            _htfMap = new Dictionary<DateTime, PurpleMetrics>(_htfMetricsList.Count);
            foreach (var m in _htfMetricsList) _htfMap[m.Date] = m;
        }
    }

    private static int ParseIntervalMinutes(string? interval) => interval switch
    {
        "D" => 1440,
        "W" => 10080,
        "M" => 43200,
        _ => int.TryParse(interval, out var minutes) && minutes > 0 ? minutes : 60,
    };

    public void RefreshLiveBar(IStrategyContext context, BarContext bar)
    {
        // [2026-05-05 20:18 +09:00] ForwardTestEngine은 성능과 상태 보존을 위해
        // 모든 전략의 Initialize()를 매 바 호출하지 않는다. 이 전략은 _ltfMap이
        // 새 확정 봉 날짜를 포함해야 하므로 라이브 확정 봉 직전에 지표 캐시만 갱신한다.
        // Initialize() 내부의 _manager 생성은 ??= 로 보호되어 있어 현재 weight/defense/peak 상태는 유지된다.
        Initialize(context);
    }

    // ── IStrategy.OnBar ──────────────────────────────────────────────

    public void OnBar(BarContext bar)
    {
        // 매 봉 시작 시 캐시 초기화 — 조기 반환 시 GetBarLog()가 _lastDebugMsg를 반환하도록 보장
        _lastInstr = null;
        _lastBarCtx = null;
        _lastDebugMsg = null;
        _lastHtfMetrics = null;
        _lastHtfDebugMsg = null;

        var i = bar.BarIndex;
        var quote = bar.Quote;
        var price = quote.Close;
        var symbol = bar.Quote.Symbol;
        // ── 진행 중인 HTF 봉 (HtfFuristic=true 일 때만 non-null)
        var liveHtf = bar.LiveHtf;

        // 현재 LTF 시점에서 가장 최근에 완성된 HTF 봉
        // [2026-05-09 +09:00] HtfHistory.Count == 0인 경우를 명시적으로 처리:
        //   - HtfInterval = null (HTF 미사용): HtfHistory는 항상 빈 리스트
        //   - 워밍업 초반: LTF 봉이 충분히 쌓이기 전에는 완성된 HTF 봉이 없을 수 있음
        //   두 경우 모두 htfMetric = null → HTF 신호 없이 LTF만으로 동작
        PurpleMetrics? htfMetric = null;
        if (bar.HtfHistory.Count > 0 && _htfMap is not null)
            _htfMap.TryGetValue(bar.HtfHistory[^1].Date, out htfMetric);
        _lastHtfMetrics = htfMetric;

        // 엔진 자동 청산(SL/TP/청산가/MaxDD) 후 Manager 상태 동기화
        if (_activeSide.HasValue && !_ctx.Positions.HasPosition(symbol, _activeSide.Value))
        {
            _manager.SyncPosition("None", 0);
            _activeSide = null;
        }

        // bar 0: 초기 equity 저장, 라이브 재시작 시 기존 포지션 Manager 동기화
        if (i == 0 || _ltfMetricsList is null)
        {
            _lastDebugMsg = i == 0
                ? null // 첫 봉은 정상 초기화이므로 로그 불필요
                : $"[Skip #{i}] _ltfMetricsList null — Initialize()의 지표 캐시 생성 실패";
            _prevEquity = bar.AccountEquity;
            foreach (var testSide in new[] { PositionSide.Long, PositionSide.Short })
            {
                var existing = _ctx.Positions.GetPosition(symbol, testSide);
                if (existing is null) continue;
                _activeSide = testSide;
                var weight = (double)(existing.Quantity * price / (bar.AccountEquity * _leverage));
                _manager.SyncPosition(testSide == PositionSide.Long ? "Long" : "Short", weight);
                break;
            }
            return;
        }

        if (_ltfMap is null || !_ltfMap.TryGetValue(quote.Date, out var ltfMetric))
        {
            _lastDebugMsg = $"[Skip #{i}] _ltfMap 날짜 미일치: bar={quote.Date:HH:mm:ss} | mapSize={_ltfMap?.Count} | last={_ltfMetricsList.LastOrDefault()?.Date:HH:mm:ss}";
            _prevEquity = bar.AccountEquity;
            return;
        }

        _lastHtfDebugMsg = BuildHtfDebugMessage(bar, ltfMetric, htfMetric);

        // Manager는 이전 봉 종료 시점 equity로 OnBarClose를 호출함.
        // bar.AccountEquity는 UpdateUnrealizedPnL 이후(현재봉 가격 반영)이므로
        // _prevEquity(직전 봉 종료값)를 전달해야 MDD 방어 발동 타이밍이 정합된다.
        var equity = _prevEquity > 0 ? _prevEquity : bar.AccountEquity;

        var instr = _manager.OnBarClose(ltfMetric, htfMetric, equity, (double)_leverage, liveHtf);

        // GetBarLog()에서 사용할 마지막 봉 상태 캐싱
        _lastBarCtx = bar;
        _lastInstr = instr;
        _lastMetrics = ltfMetric;

        ExecuteInstruction(symbol, price, equity, instr);

        _prevEquity = bar.AccountEquity;
    }

    private string BuildHtfDebugMessage(BarContext bar, PurpleMetrics ltfM, PurpleMetrics? htfMetric)
    {
        // [2026-05-06 17:26 +09:00] HTF 완성봉 정렬 확인용 로그.
        // 엔진이 제공한 bar.HtfHistory의 최신 완성봉과 전략이 실제 참조한 htfM.Date를 함께 찍어 중복 보정 여부를 확인한다.
        if (_ctx.Config.HtfInterval is null)
        {
            return $"  HTF  cfg:None  hist:{bar.HtfHistory.Count}  map:{_htfMap?.Count ?? 0}  metric:{FormatDate(htfMetric?.Date)}";
        }

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

    // ── 주문 실행 ────────────────────────────────────────────────────

    private void ExecuteInstruction(
        string symbol, decimal price, decimal equity,
        SigmoidPositionManager.OrderInstruction instr)
    {
        switch (instr.Action)
        {
            case SigmoidPositionManager.OrderAction.Hold:
                // [2026-05-03] 연속 비중 리밸런싱 — SigmoidBacktestManager 정합
                // Hold 구간에서도 equity 변동(미실현 P&L 증감)으로 인한 실효 비중 드리프트를 매 봉 교정.
                // ApplyWeight 내부 임계값(Add ≥0.0001, Reduce ≥0.001) 미만이면 거래 없음.
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
        if (weight < 0.001 || side == "None") { CloseActive(symbol, price); return; }

        var ps = side == "Long" ? PositionSide.Long : PositionSide.Short;
        decimal targetNotional = equity * _leverage * (decimal)weight;
        decimal targetQty = targetNotional / price;
        if (targetQty <= 0) return;

        var pos = _ctx.Positions.GetPosition(symbol, ps);
        if (pos is null)
        {
            var opened = _ctx.Positions.OpenPosition(symbol, ps, targetQty, _leverage, price, orderType);
            _activeSide = ps;
            if (opened is not null)
                ApplySLTP(opened);
        }
        else
        {
            decimal deltaQty = targetQty - pos.Quantity;
            if (deltaQty > 0.0001m)
            {
                //IncreasePosition >> 지정가 진입 (시장가 진입 시 슬ippage로 인해 예상보다 큰 포지션이 잡힐 수 있어, 최대한 의도한 규모에 근접하도록 지정가로 진입)
                var updated = _ctx.Positions.IncreasePosition(symbol, ps, deltaQty, price, orderType);
                if (updated is not null)
                    ApplySLTP(updated);
            }
            else if (deltaQty < -0.001m)
            {
                decimal reduceQty = -deltaQty;
                _ctx.Positions.DecreasePosition(symbol, ps, reduceQty, price, CloseReason.Decrease, orderType);
                if (!_ctx.Positions.HasPosition(symbol, ps))
                    _activeSide = null;
            }
        }
    }

    private void CloseActive(string symbol, decimal price)
    {
        if (_activeSide.HasValue && _ctx.Positions.HasPosition(symbol, _activeSide.Value))
            _ctx.Positions.CloseSide(symbol, _activeSide.Value, price);
        _activeSide = null;
    }

    private void ApplySLTP(FuturesPosition pos)
    {
        if (_lastMetrics is null) return;
        var atr = (decimal)_lastMetrics.ATR;
        var entry = pos.EntryPrice;

        if (pos.Side == PositionSide.Long)
        {
            if (_pSlMultiplier > 0) { var sl = entry - atr * _pSlMultiplier; if (sl > 0) _ctx.Positions.SetStopLoss(pos.Id, sl); }
            if (_pTpMultiplier > 0) { var tp = entry + atr * _pTpMultiplier; if (tp > 0) _ctx.Positions.SetTakeProfit(pos.Id, tp); }
        }
        else
        {
            if (_pSlMultiplier > 0) { var sl = entry + atr * _pSlMultiplier; if (sl > 0) _ctx.Positions.SetStopLoss(pos.Id, sl); }
            if (_pTpMultiplier > 0) { var tp = entry - atr * _pTpMultiplier; if (tp > 0) _ctx.Positions.SetTakeProfit(pos.Id, tp); }
        }
    }

    // ── IChartableStrategy ────────────────────────────────────────────

    public IReadOnlyList<IndicatorOverlay> GetChartOverlays() =>
    [
         new("BBU("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
        _ltfBollingerBandsPlotCache?.mBollingerBandsResults?.Select(b => b.UpperBand.HasValue ? (double)b.UpperBand.Value : double.NaN).ToArray() ?? [],2.0F,
            System.Drawing.Color.LightPink),
        new("BBC("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
        _ltfBollingerBandsPlotCache?.mBollingerBandsResults?.Select(b => b.Sma.HasValue ? (double)b.Sma.Value : double.NaN).ToArray() ?? [],3.0F,
            System.Drawing.Color.Gold),
        new("BBL("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
            _ltfBollingerBandsPlotCache?.mBollingerBandsResults?.Select(b => b.LowerBand.HasValue ? (double)b.LowerBand.Value : double.NaN).ToArray() ?? [],2.0F,
            System.Drawing.Color.SteelBlue),
        new("EMA("+SkenderUtils.mShortEmaPeriod+")",
            _ltfDualEmaPlotCache?.mShortEmaResults?.Select(e => e.Ema.HasValue ? (double)e.Ema.Value : double.NaN).ToArray() ?? [],1.5F,
            System.Drawing.Color.PaleGreen),
    ];

    // ── ILoggableStrategy ────────────────────────────────────────────

    /// <summary>
    /// 현재 봉의 상태 요약 로그를 반환한다.
    /// ForwardTestEngine이 OnBar() 직후 호출하여 LiveBarLogPanel에 전달한다.
    ///
    /// 출력 형식 (3~4줄):
    ///   ════ HH:mm:ss  #N  C:가격  Eq:자산  Avail:여유증거금 ════
    ///     [Action]  방향  W:이전→현재(Δ변화)  Net:신호  Conf:확인  Heat:과열  Energy:에너지  Defense:ON/OFF
    ///     LTF  SPb:x  SMb:x  Hist:±x  BBW:x  ATR:x
    ///     POS  방향  qty:수량  entry:진입가  UPnL:미실현손익  ROE:수익률
    /// </summary>
    public string? GetBarLog()
    {
        if (_lastInstr is null) return _lastDebugMsg; // 조기 반환 시 진단 메시지 (null이면 로그 없음)
        if (_lastBarCtx is null) return null;

        var bar = _lastBarCtx;
        var instr = _lastInstr;
        var q = bar.Quote;
        var symbol = q.Symbol;

        // 현재 포지션 조회 (ExecuteInstruction 실행 후 _activeSide 기준)
        FuturesPosition? pos = _activeSide.HasValue
            ? _ctx.Positions.GetPosition(symbol, _activeSide.Value)
            : null;

        var prevWeight = instr.CurrentWeight - instr.WeightDelta;
        var defense = _manager?.InDefense == true ? "ON" : "OFF";
        var actionTag = $"[{instr.Action}]";

        var sb = new System.Text.StringBuilder();

        // 헤더 줄 — 타임스탬프, 봉 번호, 현재가, 계좌 상태
        sb.AppendLine(
            $"════ {q.Date:HH:mm:ss}  #{bar.BarIndex}  C:{q.Close:N0}  " +
            $"Eq:{bar.AccountEquity:N2}  Avail:{bar.AvailableMargin:N2} ════");

        // 신호 줄 — 포지션 결정, 방향, 웨이트 변화, 스코어
        sb.AppendLine(
            $"  {actionTag,-22}  {instr.Side,-5}  " +
            $"W:{prevWeight:F3}→{instr.CurrentWeight:F3}(Δ{instr.WeightDelta:+0.000;-0.000;0.000})  " +
            $"Final Scoe:{instr.FinalScore:N0}  Net:{instr.NetSignal:F2}  Conf:{instr.ConfirmScore:F2}  " +
            $"Heat:{instr.HeatScore:F2}  Energy:{instr.EnergyScore:F2}  Defense:{defense}");

        // LTF 지표 줄
        if (_lastMetrics is { } m)
        {
            sb.AppendLine(
                $"  LTF  SPb:{m.SPb:F3}  SMb:{m.SMb:F3}  " +
                $"Hist:{m.Histogram:+0.0000;-0.0000;0.0000}  " +
                $"BBW:{m.BBW:F4}  ATR:{m.ATR:F1}");
        }

        if (_lastHtfDebugMsg is not null)
        {
            sb.AppendLine(_lastHtfDebugMsg);
            if (_lastHtfMetrics is { } htf)
            {
                sb.AppendLine(
                    $"  HTF  SPb:{htf.SPb:F3}  SMb:{htf.SMb:F3}  " +
                    $"Hist:{htf.Histogram:+0.0000;-0.0000;0.0000}  " +
                    $"BBW:{htf.BBW:F4}  ATR:{htf.ATR:F1}");
            }
        }

        // 포지션 줄
        if (pos is not null)
        {
            sb.Append(
                $"  POS  {pos.Side,-5}  qty:{pos.Quantity:F4}  entry:{pos.EntryPrice:N0}  " +
                $"UPnL:{pos.UnrealizedPnL:+0.00;-0.00;0.00}  ROE:{pos.Roe:+0.00%;-0.00%;0.00%}");
            if (pos.StopLoss > 0 || pos.TakeProfit > 0)
            {
                sb.Append($"  SL:{(pos.StopLoss > 0 ? pos.StopLoss.ToString("N0") : "—")}");
                sb.Append($"  TP:{(pos.TakeProfit > 0 ? pos.TakeProfit.ToString("N0") : "—")}");
            }
        }
        else
        {
            sb.Append("  POS  (없음)");
        }

        return sb.ToString();
    }

    // ── 이벤트 ───────────────────────────────────────────────────────

    public void OnPositionOpened(FuturesPosition p) =>
        Console.WriteLine($"  [OPEN ] {p.Side,-5} {p.Quantity:F4} @ {p.EntryPrice:N0}");

    public void OnPositionClosed(FuturesPosition p, Trade t) =>
        Console.WriteLine($"  [CLOSE] {p.Side,-5} {t.Quantity:F4} @ {t.ExitPrice:N0}" +
                          $"  PnL={(t.NetPnL >= 0 ? "+" : "")}{t.NetPnL:F2}");
}