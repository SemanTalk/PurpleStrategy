using CryptoFuturesBacktester.Core.Contracts;
using CryptoFuturesBacktester.Core.Enums;
using CryptoFuturesBacktester.Core.Models;
using PurpleStrategy.Models;
using PurpleStrategy.Sigmoid;
using PurpleStrategy.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UserStrategy;

// [2026-05-14] 본체 per-bar 파이프라인을 SigmoidStrategyRunner 로 위임.
// Logic Canvas Action.SigmoidDispatcher 노드와 동일 헬퍼를 wrap → 동작 lockstep.
// HTF axis = LR Slope/Close + R² gate (MACD Trend axis 폐기).
public sealed class SigmoidWeightStrategy : IParametrizedStrategy, IChartableStrategy, ILoggableStrategy, ILiveBarRefreshStrategy
{
    private readonly SigmoidStrategyRunner _runner = new();
    private IStrategyContext _ctx = default!;

    // ── 파라미터 저장 필드 (Initialize 전에 SetParameters 호출됨)
    private decimal _pLeverage = 10m;
    private decimal _pSlMultiplier = 0m;
    private decimal _pTpMultiplier = 0m;
    private decimal _pSteepness = 3m;
    private decimal _pTrendFullScale = 0.003m;
    private decimal _pMinTrendMagnitude = 0.0005m;
    private decimal _pMinRSquared = 0.5m;
    private decimal _pBBWMinThreshold = 20m;
    private decimal _pMddDefenseThreshold = 0.15m;
    private decimal _pMddDefenseMaxWeight = 0.10m;
    private decimal _pMddDefenseExitThreshold = 0.05m;
    private decimal _pMaxWeightChangePerBar = 0.002m;
    private decimal _pExitSpeedMultiplier = 100m;
    private decimal _pMaxWeight = 0.3m;

    public string Name => $"Sigmoid  Lev={_pLeverage}x  MaxW={_pMaxWeight:P0}";

    // ── IParametrizedStrategy ─────────────────────────────────────────

    public IReadOnlyList<StrategyParameter> GetParameters() =>
    [
        new("Steepness","Steepness",       StrategyParamType.Decimal,  _pSteepness,  0m, 15m,   1m, 1),
        new("TrendFullScale","TrendFullScale (LR |Slope/Close| sat)", StrategyParamType.Decimal, _pTrendFullScale, 0.001m, 0.010m, 0.001m, 4),
        new("MinTrendMagnitude",    "MinTrendMagnitude (LR |Slope/Close| min)",     StrategyParamType.Decimal,  _pMinTrendMagnitude,  0.0m,  0.005m,   0.0005m, 4),
        new("MinRSquared",    "MinRSquared (LR confidence gate)",     StrategyParamType.Decimal,  _pMinRSquared,  0.0m,  1.0m,   0.05m, 2),
        new("BBWMinThreshold",    "BBWMinThreshold",     StrategyParamType.Decimal,  _pBBWMinThreshold,  15m,  30m,   1m, 0),
        new("Leverage",    "Leverage",           StrategyParamType.Decimal,  _pLeverage,  1m, 10m, 2m, 0),
        new("MddDefenseThreshold", "MddDefenseThreshold", StrategyParamType.Decimal, _pMddDefenseThreshold, 0.0m, 1.0m, 0.5m, 1),
        new("MddDefenseMaxWeight", "MddDefenseMaxWeight", StrategyParamType.Decimal, _pMddDefenseMaxWeight, 0.0m, 1.0m, 0.5m, 1),
        new("MddDefenseExitThreshold", "MddDefenseExitThreshold", StrategyParamType.Decimal, _pMddDefenseExitThreshold, 0.0m, 1.0m, 0.05m, 2),
        new("MaxWeightChangePerBar","MaxWeightChangePerBar",       StrategyParamType.Decimal,  _pMaxWeightChangePerBar,  0.001m, 0.010m, 0.002m, 3),
        new("ExitSpeedMultiplier","ExitSpeedMultiplier",       StrategyParamType.Decimal,  _pExitSpeedMultiplier,  1m, 100m, 10m, 0),
        new("MaxWeight",   "MaxWeight",        StrategyParamType.Decimal, _pMaxWeight, 0.1m, 0.5m,  0.1m, 1),
        new("SlMultiplier","SL Multiplier (ATR×)", StrategyParamType.Decimal, _pSlMultiplier, 0m, 5m, 1m, 0),
        new("TpMultiplier","TP Multiplier (ATR×)", StrategyParamType.Decimal, _pTpMultiplier, 0m, 10m, 2m, 0)
    ];

    public void SetParameters(IReadOnlyDictionary<string, decimal> values)
    {
        if (values.TryGetValue("Leverage", out var v)) _pLeverage = v;
        if (values.TryGetValue("Steepness", out v)) _pSteepness = v;
        if (values.TryGetValue("TrendFullScale", out v)) _pTrendFullScale = v;
        if (values.TryGetValue("MinTrendMagnitude", out v)) _pMinTrendMagnitude = v;
        if (values.TryGetValue("MinRSquared", out v)) _pMinRSquared = v;
        if (values.TryGetValue("MaxWeightChangePerBar", out v)) _pMaxWeightChangePerBar = v;
        if (values.TryGetValue("BBWMinThreshold", out v)) _pBBWMinThreshold = v;

        if (values.TryGetValue("MddDefenseThreshold", out v)) _pMddDefenseThreshold = v;
        if (values.TryGetValue("MddDefenseMaxWeight", out v)) _pMddDefenseMaxWeight = v;
        if (values.TryGetValue("MddDefenseExitThreshold", out v)) _pMddDefenseExitThreshold = v;
        if (values.TryGetValue("ExitSpeedMultiplier", out v)) _pExitSpeedMultiplier = v;

        if (values.TryGetValue("MaxWeight", out v)) _pMaxWeight = v;
        if (values.TryGetValue("SlMultiplier", out v)) _pSlMultiplier = v;
        if (values.TryGetValue("TpMultiplier", out v)) _pTpMultiplier = v;
    }

    // ── IStrategy ─────────────────────────────────────────────────────

    public void Initialize(IStrategyContext context)
    {
        _ctx = context;

        _runner.Leverage = _pLeverage;
        _runner.SlMultiplier = _pSlMultiplier;
        _runner.TpMultiplier = _pTpMultiplier;

        _runner.Initialize(_ctx, new SigmoidParams
        {
            Steepness = (double)_pSteepness,
            TrendFullScale = (double)_pTrendFullScale,
            MinTrendMagnitude = (double)_pMinTrendMagnitude,
            MinRSquared = (double)_pMinRSquared,
            MaxWeightChangePerBar = (double)_pMaxWeightChangePerBar,
            BBWMinThreshold = (double)_pBBWMinThreshold,
            MddDefenseThreshold = (double)_pMddDefenseThreshold,
            MddDefenseMaxWeight = (double)_pMddDefenseMaxWeight,
            MddDefenseExitThreshold = (double)_pMddDefenseExitThreshold,
            ExitSpeedMultiplier = (double)_pExitSpeedMultiplier,
            MaxWeight = (double)_pMaxWeight,
        });
    }

    public void RefreshLiveBar(IStrategyContext context, BarContext bar)
    {
        Initialize(context);
    }

    public void OnBar(BarContext bar) => _runner.OnBar(bar, signalGate: true);

    // ── IChartableStrategy ────────────────────────────────────────────

    public IReadOnlyList<IndicatorOverlay> GetChartOverlays() =>
    [
         new("BBU("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
        _runner.LtfBollingerBands?.mBollingerBandsResults?.Select(b => b.UpperBand.HasValue ? (double)b.UpperBand.Value : double.NaN).ToArray() ?? [],2.0F,
            System.Drawing.Color.LightPink),
        new("BBC("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
        _runner.LtfBollingerBands?.mBollingerBandsResults?.Select(b => b.Sma.HasValue ? (double)b.Sma.Value : double.NaN).ToArray() ?? [],3.0F,
            System.Drawing.Color.Gold),
        new("BBL("+SkenderUtils.mBasePeriod+","+SkenderUtils.mStdv+")",
            _runner.LtfBollingerBands?.mBollingerBandsResults?.Select(b => b.LowerBand.HasValue ? (double)b.LowerBand.Value : double.NaN).ToArray() ?? [],2.0F,
            System.Drawing.Color.SteelBlue),
    ];

    // ── ILoggableStrategy ────────────────────────────────────────────

    public string? GetBarLog()
    {
        var instr = _runner.LastInstr;
        if (instr is null) return _runner.LastDebugMsg;
        var bar = _runner.LastBarCtx;
        if (bar is null) return null;

        var q = bar.Quote;
        var symbol = q.Symbol;

        FuturesPosition? pos = _runner.ActiveSide.HasValue
            ? _ctx.Positions.GetPosition(symbol, _runner.ActiveSide.Value)
            : null;

        var prevWeight = instr.CurrentWeight - instr.WeightDelta;
        var defense = _runner.Manager?.InDefense == true ? "ON" : "OFF";
        var actionTag = $"[{instr.Action}]";

        var sb = new System.Text.StringBuilder();

        sb.AppendLine(
            $"════ {q.Date:HH:mm:ss}  #{bar.BarIndex}  C:{q.Close:N0}  " +
            $"Eq:{bar.AccountEquity:N2}  Avail:{bar.AvailableMargin:N2} ════");

        sb.AppendLine(
            $"  {actionTag,-22}  {instr.Side,-5}  " +
            $"W:{prevWeight:F3}→{instr.CurrentWeight:F3}(Δ{instr.WeightDelta:+0.000;-0.000;0.000})  " +
            $"Trend:{instr.Trend:+0.0000;-0.0000;0.0000}  Str:{instr.Strength:F2}  " +
            $"MaxW:{instr.MaxWeight:P0}  Defense:{defense}");

        if (_runner.LastMetrics is { } m)
        {
            sb.AppendLine(
                $"  LTF  SPb:{m.SPb:F3}  SMb:{m.SMb:F3}  " +
                $"Hist:{m.Histogram:+0.0000;-0.0000;0.0000}  " +
                $"BBW:{m.BBW:F4}  ATR:{m.ATR:F1}");
        }

        if (_runner.LastHtfDebugMsg is not null)
        {
            sb.AppendLine(_runner.LastHtfDebugMsg);
            if (_runner.LastHtfMetrics is { } htf)
            {
                sb.AppendLine(
                    $"  HTF  SPb:{htf.SPb:F3}  SMb:{htf.SMb:F3}  " +
                    $"Hist:{htf.Histogram:+0.0000;-0.0000;0.0000}  " +
                    $"BBW:{htf.BBW:F4}  ATR:{htf.ATR:F1}");
            }
        }

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
