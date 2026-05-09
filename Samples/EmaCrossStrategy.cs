using System;
using System.Collections.Generic;
using System.Linq;
using CryptoFuturesBacktester.Application;
using CryptoFuturesBacktester.Core.Contracts;
using CryptoFuturesBacktester.Core.Enums;
using CryptoFuturesBacktester.Core.Models;
using Skender.Stock.Indicators;

namespace UserStrategy;

/// <summary>
/// EMA 골든/데스 크로스 전략.
/// IParametrizedStrategy를 구현하여 UI에서 4개 파라미터를 동적으로 편집할 수 있다.
///
/// [파라미터 표시 단위 규칙]
/// RiskPercent: UI 표시값 1.0 = 1%, 내부 저장은 비율 0.01.
/// Leverage: 표시값 = 내부값 (배수 그대로).
/// </summary>
public sealed class EmaCrossStrategy : IParametrizedStrategy, IChartableStrategy
{
    // ── 파라미터 필드 (SetParameters로 주입 가능, 기본값 설정됨) ────────────────
    private int _fastPeriod = 20;

    private int _slowPeriod = 50;
    private decimal _riskPercent = 0.01m;  // 내부 저장은 비율 (1% = 0.01)
    private decimal _leverage = 5m;

    private IStrategyContext _ctx = default!;
    private IReadOnlyList<EmaResult> _fast = default!;
    private IReadOnlyList<EmaResult> _slow = default!;

    public string Name =>
        $"EMA Cross ({_fastPeriod}/{_slowPeriod}) R={_riskPercent:P0} L={_leverage}x";

    // ── 생성자 ──────────────────────────────────────────────────────────────

    // 파라미터 없는 생성자: Roslyn 인스턴스화 + SetParameters() 주입 경로
    public EmaCrossStrategy()
    { }

    // 직접 생성 경로 (기존 코드 호환, riskPercent는 비율 단위)
    public EmaCrossStrategy(
        int fastPeriod = 20,
        int slowPeriod = 50,
        decimal riskPercent = 0.01m,
        decimal leverage = 5m)
    {
        _fastPeriod = fastPeriod;
        _slowPeriod = slowPeriod;
        _riskPercent = riskPercent;
        _leverage = leverage;
    }

    // ── IParametrizedStrategy ───────────────────────────────────────────────

    public IReadOnlyList<StrategyParameter> GetParameters() =>
    [
        new("FastPeriod",  "Fast EMA",  StrategyParamType.Int,      20m,  1m,   500m,  1m,  0),
        new("SlowPeriod",  "Slow EMA",  StrategyParamType.Int,      50m,  2m,   500m,  1m,  0),
        new("RiskPercent", "Risk %",    StrategyParamType.Decimal,   1.0m, 0.01m, 20m,  0.1m, 2),
        new("Leverage",    "Leverage",  StrategyParamType.Decimal,   5m,  1m,   125m,  1m,  0),
    ];

    /// <summary>
    /// values는 표시 단위. RiskPercent: 1.0 → 내부에서 0.01로 변환.
    /// </summary>
    public void SetParameters(IReadOnlyDictionary<string, decimal> values)
    {
        if (values.TryGetValue("FastPeriod", out var fp)) _fastPeriod = (int)fp;
        if (values.TryGetValue("SlowPeriod", out var sp)) _slowPeriod = (int)sp;
        if (values.TryGetValue("RiskPercent", out var rp)) _riskPercent = rp / 100m;
        if (values.TryGetValue("Leverage", out var lev)) _leverage = lev;
    }

    // ── IStrategy ───────────────────────────────────────────────────────────

    public void Initialize(IStrategyContext context)
    {
        // [2026-05-05 20:18 +09:00] 전략은 최초 1회 지표를 요청하면 된다.
        // 라이브에서 새 확정 봉이 추가될 때의 캐시 갱신은 StrategyContext가 담당한다.
        _ctx = context;
        _fast = context.GetIndicator($"EMA_{_fastPeriod}", q => q.GetEma(_fastPeriod).ToList());
        _slow = context.GetIndicator($"EMA_{_slowPeriod}", q => q.GetEma(_slowPeriod).ToList());
    }

    public void OnBar(BarContext bar)
    {
        var i = bar.BarIndex;
        if (i < 1) return;

        var fastNow = _fast[i].Ema;
        var fastPrev = _fast[i - 1].Ema;
        var slowNow = _slow[i].Ema;
        var slowPrev = _slow[i - 1].Ema;

        if (fastNow is null || fastPrev is null || slowNow is null || slowPrev is null) return;

        var positions = _ctx.Positions;
        var symbol = bar.Quote.Symbol;
        var price = bar.Quote.Close;
        var goldenCross = fastPrev <= slowPrev && fastNow > slowNow;
        var deathCross = fastPrev >= slowPrev && fastNow < slowNow;

        if (goldenCross && !positions.HasPosition(symbol, PositionSide.Long))
        {
            var stopLoss = bar.History[i - 1].Low;
            var qty = new RiskManager(_ctx.Config)
                               .CalculatePositionSize(bar.AccountEquity, _riskPercent, price, stopLoss, _leverage);
            if (qty <= 0) return;

            var pos = positions.OpenPosition(symbol, PositionSide.Long, qty, _leverage, price);
            if (pos is not null)
                positions.SetStopLoss(pos.Id, stopLoss);
        }
        else if (deathCross && positions.HasPosition(symbol, PositionSide.Long))
        {
            positions.CloseSide(symbol, PositionSide.Long, price);
        }
    }

    // ── IChartableStrategy ──────────────────────────────────────────────────

    public IReadOnlyList<IndicatorOverlay> GetChartOverlays() =>
    [
        new($"EMA {_fastPeriod}",
            _fast?.Select(e => e.Ema.HasValue ? (double)e.Ema.Value : double.NaN).ToArray() ?? [],1.5F,
            System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xA5, 0x00)),   // 앰버
        new($"EMA {_slowPeriod}",
            _slow?.Select(e => e.Ema.HasValue ? (double)e.Ema.Value : double.NaN).ToArray() ?? [],1.5F,
            System.Drawing.Color.FromArgb(0xFF, 0x5B, 0x8D, 0xFF)),   // 스틸 블루
    ];

    public void OnPositionOpened(FuturesPosition position)
    {
        Console.WriteLine($"  [OPEN ] {position.Side,-5} {position.Quantity:F4} @ {position.EntryPrice:N0}" +
                          $"  SL={position.StopLoss:N0}  Margin={position.Margin:F2}");
    }

    public void OnPositionClosed(FuturesPosition position, Trade trade)
    {
        var sign = trade.NetPnL >= 0 ? "+" : "";
        Console.WriteLine($"  [CLOSE] {position.Side,-5} {trade.Quantity:F4} @ {trade.ExitPrice:N0}" +
                          $"  PnL={sign}{trade.NetPnL:F2}  ({trade.CloseReason})");
    }
}