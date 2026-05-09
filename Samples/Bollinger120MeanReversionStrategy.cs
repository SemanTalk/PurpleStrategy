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
/// LTF/HTF 볼린저밴드 평균회귀 전략.
/// LTF 밴드 밖으로 과도하게 이탈한 가격이 다시 밴드 안쪽으로 들어올 때 진입하고,
/// LTF 중심선 회귀를 목표로 청산한다. HTF 밴드는 큰 시간대 위치 필터로 사용한다.
/// </summary>
public sealed class Bollinger120MeanReversionStrategy : IParametrizedStrategy, IChartableStrategy, ILoggableStrategy
{
    private int _ltfPeriod = 120;
    private decimal _ltfStdDev = 2.0m;
    private int _htfPeriod = 120;
    private decimal _htfStdDev = 2.0m;
    private decimal _riskPercent = 0.01m;
    private decimal _leverage = 5m;

    private const decimal StopBandWidthMultiple = 0.50m;

    private IStrategyContext _ctx = default!;
    private IReadOnlyList<BollingerBandsResult> _bb = default!;
    private Dictionary<DateTime, Band> _htfBandsByDate = [];
    private string? _barLog;

    public string Name =>
        $"BB Mean Reversion LTF({_ltfPeriod}, {_ltfStdDev:F1}) HTF({_htfPeriod}, {_htfStdDev:F1}) R={_riskPercent:P0} L={_leverage}x";

    public Bollinger120MeanReversionStrategy()
    { }

    public Bollinger120MeanReversionStrategy(
        int ltfPeriod = 120,
        decimal ltfStdDev = 2.0m,
        int htfPeriod = 120,
        decimal htfStdDev = 2.0m,
        decimal riskPercent = 0.01m,
        decimal leverage = 5m)
    {
        _ltfPeriod = ltfPeriod;
        _ltfStdDev = ltfStdDev;
        _htfPeriod = htfPeriod;
        _htfStdDev = htfStdDev;
        _riskPercent = riskPercent;
        _leverage = leverage;
        NormalizeParameters();
    }

    public IReadOnlyList<StrategyParameter> GetParameters() =>
    [
        new("LtfPeriod", "LTF BB Period", StrategyParamType.Int, 120m, 20m, 300m, 5m, 0),
        new("LtfStdDev", "LTF Std Dev", StrategyParamType.Decimal, 2.0m, 1.0m, 4.0m, 0.1m, 1),
        new("HtfPeriod", "HTF BB Period", StrategyParamType.Int, 120m, 20m, 300m, 5m, 0),
        new("HtfStdDev", "HTF Std Dev", StrategyParamType.Decimal, 2.0m, 1.0m, 4.0m, 0.1m, 1),
        new("RiskPercent", "Risk %", StrategyParamType.Decimal, 1.0m, 0.1m, 5m, 0.1m, 1),
        new("Leverage", "Leverage", StrategyParamType.Decimal, 5m, 1m, 50m, 1m, 0),
    ];

    /// <summary>
    /// UI 표시 단위 기준으로 파라미터를 받는다. RiskPercent: 1.0 -> 내부 0.01.
    /// </summary>
    public void SetParameters(IReadOnlyDictionary<string, decimal> values)
    {
        // 이전 단일 BB 파라미터 저장값이 들어오면 LTF/HTF 양쪽 기본값으로 적용하고,
        // 새 LTF/HTF 개별 키가 있으면 아래에서 다시 덮어쓴다.
        if (values.TryGetValue("Period", out var legacyPeriod))
        {
            _ltfPeriod = (int)legacyPeriod;
            _htfPeriod = (int)legacyPeriod;
        }

        if (values.TryGetValue("StdDev", out var legacyStdDev))
        {
            _ltfStdDev = legacyStdDev;
            _htfStdDev = legacyStdDev;
        }

        if (values.TryGetValue("LtfPeriod", out var ltfPeriod)) _ltfPeriod = (int)ltfPeriod;
        if (values.TryGetValue("LtfStdDev", out var ltfStdDev)) _ltfStdDev = ltfStdDev;
        if (values.TryGetValue("HtfPeriod", out var htfPeriod)) _htfPeriod = (int)htfPeriod;
        if (values.TryGetValue("HtfStdDev", out var htfStdDev)) _htfStdDev = htfStdDev;
        if (values.TryGetValue("RiskPercent", out var riskPercent)) _riskPercent = riskPercent / 100m;
        if (values.TryGetValue("Leverage", out var leverage)) _leverage = leverage;

        NormalizeParameters();
    }

    public void Initialize(IStrategyContext context)
    {
        _ctx = context;
        _bb = context.GetIndicator(
            $"LTF_BB_MR_{_ltfPeriod}_{_ltfStdDev:F1}",
            q => q.GetBollingerBands(_ltfPeriod, (double)_ltfStdDev).ToList());

        // HTF 밴드는 Context.GetIndicator가 LTF QuoteHistory 전용이므로 HTF 히스토리에서 직접 계산한다.
        // OnBar에서는 bar.HtfHistory의 마지막 완성봉 날짜로 조회하여 미래 HTF 봉을 참조하지 않는다.
        _htfBandsByDate = [];
        foreach (var result in context.HtfQuoteHistory.GetBollingerBands(_htfPeriod, (double)_htfStdDev))
        {
            var band = ToBand(result);
            if (band is not null)
                _htfBandsByDate[result.Date] = band.Value;
        }
    }

    public void OnBar(BarContext bar)
    {
        _barLog = null;

        var i = bar.BarIndex;
        if (i < 1 || i >= _bb.Count) return;

        var current = ToBand(_bb[i]);
        var previous = ToBand(_bb[i - 1]);
        if (current is null || previous is null) return;

        var symbol = bar.Quote.Symbol;
        var price = bar.Quote.Close;
        var longPosition = _ctx.Positions.GetPosition(symbol, PositionSide.Long);
        var shortPosition = _ctx.Positions.GetPosition(symbol, PositionSide.Short);

        if (longPosition is not null)
        {
            ManageLong(longPosition, price, current.Value);
            return;
        }

        if (shortPosition is not null)
        {
            ManageShort(shortPosition, price, current.Value);
            return;
        }

        // 이전 봉이 하단 밴드 밖에서 끝났고 현재 봉이 다시 밴드 안쪽으로 복귀하면 평균회귀 Long.
        var longReentry = bar.History[i - 1].Close < previous.Value.Lower && price > current.Value.Lower;

        // 이전 봉이 상단 밴드 밖에서 끝났고 현재 봉이 다시 밴드 안쪽으로 복귀하면 평균회귀 Short.
        var shortReentry = bar.History[i - 1].Close > previous.Value.Upper && price < current.Value.Upper;

        var htfBand = GetLatestHtfBand(bar);
        if (htfBand is null)
        {
            _barLog = $"BB MR skip: HTF band unavailable hist={bar.HtfHistory.Count}";
            return;
        }

        // HTF 밴드 위치는 큰 시간대 필터다.
        // Long은 HTF 하단 절반, Short은 HTF 상단 절반에서만 허용해 큰 흐름의 극단 회귀에 맞춘다.
        var htfPosition = GetBandPosition(price, htfBand.Value);
        var htfAllowsLong = htfPosition <= 0.50m;
        var htfAllowsShort = htfPosition >= 0.50m;

        if (longReentry && htfAllowsLong)
            OpenMeanReversionPosition(symbol, PositionSide.Long, price, current.Value, bar.AccountEquity);
        else if (shortReentry && htfAllowsShort)
            OpenMeanReversionPosition(symbol, PositionSide.Short, price, current.Value, bar.AccountEquity);
        else
            _barLog = $"BB MR hold price={price:N2} ltf=({current.Value.Lower:N2}/{current.Value.Middle:N2}/{current.Value.Upper:N2}) htfPos={htfPosition:F2}";
    }

    public IReadOnlyList<IndicatorOverlay> GetChartOverlays() =>
    [
        new($"LTF BB Upper {_ltfPeriod}", ToArray(b => b.UpperBand), 1.2F,
            System.Drawing.Color.FromArgb(0xFF, 0x66, 0xCC, 0xFF)),
        new($"LTF BB SMA {_ltfPeriod}", ToArray(b => b.Sma), 1.4F,
            System.Drawing.Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8)),
        new($"LTF BB Lower {_ltfPeriod}", ToArray(b => b.LowerBand), 1.2F,
            System.Drawing.Color.FromArgb(0xFF, 0xFF, 0x99, 0x66)),
    ];

    public string? GetBarLog() => _barLog;

    public void OnPositionOpened(FuturesPosition position)
    {
        Console.WriteLine($"  [OPEN ] {position.Side,-5} {position.Quantity:F4} @ {position.EntryPrice:N0}" +
                          $"  SL={position.StopLoss:N0} TP={position.TakeProfit:N0}");
    }

    public void OnPositionClosed(FuturesPosition position, Trade trade)
    {
        var sign = trade.NetPnL >= 0 ? "+" : "";
        Console.WriteLine($"  [CLOSE] {position.Side,-5} {trade.Quantity:F4} @ {trade.ExitPrice:N0}" +
                          $"  PnL={sign}{trade.NetPnL:F2} ({trade.CloseReason})");
    }

    private void OpenMeanReversionPosition(string symbol, PositionSide side, decimal price, Band band, decimal accountEquity)
    {
        var bandWidth = band.Upper - band.Lower;
        if (bandWidth <= 0) return;

        // 손절은 재이탈 여지를 조금 허용하기 위해 진입 밴드 바깥쪽에 밴드폭의 일부만큼 둔다.
        var stopPrice = side == PositionSide.Long
            ? band.Lower - bandWidth * StopBandWidthMultiple
            : band.Upper + bandWidth * StopBandWidthMultiple;

        if (stopPrice <= 0 || stopPrice == price) return;

        var quantity = new RiskManager(_ctx.Config)
            .CalculatePositionSize(accountEquity, _riskPercent, price, stopPrice, _leverage);

        if (quantity <= 0) return;

        var opened = _ctx.Positions.OpenPosition(symbol, side, quantity, _leverage, price);
        if (opened is null) return;

        _ctx.Positions.SetStopLoss(opened.Id, stopPrice);
        _ctx.Positions.SetTakeProfit(opened.Id, band.Middle);
        _barLog = $"BB MR open {side} price={price:N2} target={band.Middle:N2} stop={stopPrice:N2}";
    }

    private void ManageLong(FuturesPosition position, decimal price, Band band)
    {
        // 평균회귀 목표인 중심선에 도달하면 청산한다. 엔진 TP와 중복되어도 명시적으로 신호 청산을 보장한다.
        if (price >= band.Middle)
        {
            _ctx.Positions.ClosePosition(position.Id, price);
            _barLog = $"BB MR close Long at mean price={price:N2} mean={band.Middle:N2}";
        }
    }

    private void ManageShort(FuturesPosition position, decimal price, Band band)
    {
        if (price <= band.Middle)
        {
            _ctx.Positions.ClosePosition(position.Id, price);
            _barLog = $"BB MR close Short at mean price={price:N2} mean={band.Middle:N2}";
        }
    }

    private Band? GetLatestHtfBand(BarContext bar)
    {
        if (bar.HtfHistory.Count == 0)
            return null;

        return _htfBandsByDate.TryGetValue(bar.HtfHistory[^1].Date, out var band)
            ? band
            : null;
    }

    private static decimal GetBandPosition(decimal price, Band band)
    {
        var width = band.Upper - band.Lower;
        if (width <= 0) return 0.5m;

        return Math.Clamp((price - band.Lower) / width, 0m, 1m);
    }

    private void NormalizeParameters()
    {
        _ltfPeriod = Math.Max(2, _ltfPeriod);
        _ltfStdDev = Math.Clamp(_ltfStdDev, 0.1m, 10m);
        _htfPeriod = Math.Max(2, _htfPeriod);
        _htfStdDev = Math.Clamp(_htfStdDev, 0.1m, 10m);
        _riskPercent = Math.Clamp(_riskPercent, 0.0001m, 1m);
        _leverage = Math.Max(1m, _leverage);
    }

    private Band? ToBand(BollingerBandsResult result)
    {
        if (result.UpperBand is null || result.Sma is null || result.LowerBand is null)
            return null;

        return new Band(
            Upper: (decimal)result.UpperBand.Value,
            Middle: (decimal)result.Sma.Value,
            Lower: (decimal)result.LowerBand.Value);
    }

    private double[] ToArray(Func<BollingerBandsResult, double?> selector) =>
        _bb?.Select(b => selector(b) ?? double.NaN).ToArray() ?? [];

    private readonly record struct Band(decimal Upper, decimal Middle, decimal Lower);
}
