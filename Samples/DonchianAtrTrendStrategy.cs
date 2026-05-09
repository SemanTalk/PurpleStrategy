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
/// [2026-05-06 00:17 +09:00] 돈치안 채널 돌파와 ATR 단위 리스크를 결합한 추세추종 샘플 전략.
/// Richard Dennis/William Eckhardt의 터틀 트레이딩 원형에 가까운 보편적 구조를 사용한다.
///
/// 사용법:
/// 1. EntryPeriod는 진입 돌파 채널 길이, ExitPeriod는 청산 채널 길이로 사용한다.
/// 2. RiskPercent는 전체 피라미딩 계획의 총 허용 리스크이며, 내부에서는 4개 유닛으로 나눠 사용한다.
/// 3. ADX(14) >= 20, EMA(200) 방향성이 맞는 구간에서만 신규 진입과 증량을 허용한다.
/// 4. 같은 방향 추세가 이어질 때 IncreasePosition으로 매집하고, 추세 약화/청산 채널 이탈 시 DecreasePosition으로 분산 청산한다.
/// </summary>
public sealed class DonchianAtrTrendStrategy : IParametrizedStrategy, IChartableStrategy
{
    // [2026-05-06 00:17 +09:00] 최적화 대상 파라미터는 요구사항에 맞춰 4개로 제한한다.
    private int _entryPeriod = 55;
    private int _exitPeriod = 20;
    private decimal _riskPercent = 0.01m;
    private decimal _leverage = 5m;

    // [2026-05-06 00:17 +09:00] 추세 필터와 포지션 운용 규칙은 과최적화를 피하려고 고정값으로 둔다.
    private const int AtrPeriod = 20;
    private const int AdxPeriod = 14;
    private const int TrendEmaPeriod = 200;
    private const int MaxUnits = 4;
    private const decimal MinAdx = 20m;
    private const decimal AtrStopMultiple = 2m;
    private const decimal PyramidAtrStep = 0.5m;

    private IStrategyContext _ctx = default!;
    private IReadOnlyList<AtrResult> _atr = default!;
    private IReadOnlyList<AdxResult> _adx = default!;
    private IReadOnlyList<EmaResult> _trendEma = default!;
    private IReadOnlyList<ChannelPoint> _channels = default!;

    // [2026-05-06 00:17 +09:00] 피라미딩 상태는 현재 열려 있는 한 포지션의 유닛 규모와 마지막 증량 가격을 추적한다.
    private decimal _unitQty;
    private decimal _lastAddPrice;
    private int _unitCount;
    private bool _isScalingOut;

    public string Name =>
        $"Donchian ATR Trend ({_entryPeriod}/{_exitPeriod}) R={_riskPercent:P0} L={_leverage}x";

    public DonchianAtrTrendStrategy()
    { }

    public DonchianAtrTrendStrategy(
        int entryPeriod = 55,
        int exitPeriod = 20,
        decimal riskPercent = 0.01m,
        decimal leverage = 5m)
    {
        _entryPeriod = entryPeriod;
        _exitPeriod = exitPeriod;
        _riskPercent = riskPercent;
        _leverage = leverage;
        NormalizePeriods();
    }

    public IReadOnlyList<StrategyParameter> GetParameters() =>
    [
        new("EntryPeriod", "Entry Donchian", StrategyParamType.Int, 55m, 10m, 200m, 5m, 0),
        new("ExitPeriod", "Exit Donchian", StrategyParamType.Int, 20m, 5m, 100m, 5m, 0),
        new("RiskPercent", "Risk %", StrategyParamType.Decimal, 1.0m, 0.1m, 5m, 0.1m, 1),
        new("Leverage", "Leverage", StrategyParamType.Decimal, 5m, 1m, 50m, 1m, 0),
    ];

    /// <summary>
    /// [2026-05-06 00:17 +09:00] UI는 RiskPercent를 퍼센트 표시 단위로 넘기므로 1.0을 내부 비율 0.01로 변환한다.
    /// </summary>
    public void SetParameters(IReadOnlyDictionary<string, decimal> values)
    {
        if (values.TryGetValue("EntryPeriod", out var ep)) _entryPeriod = (int)ep;
        if (values.TryGetValue("ExitPeriod", out var xp)) _exitPeriod = (int)xp;
        if (values.TryGetValue("RiskPercent", out var rp)) _riskPercent = rp / 100m;
        if (values.TryGetValue("Leverage", out var lev)) _leverage = lev;

        NormalizePeriods();
    }

    public void Initialize(IStrategyContext context)
    {
        // [2026-05-06 00:17 +09:00] 모든 지표는 Initialize에서 한 번 계산하고, OnBar에서는 현재 인덱스만 조회한다.
        _ctx = context;
        _atr = context.GetIndicator($"ATR_{AtrPeriod}", q => q.GetAtr(AtrPeriod).ToList());
        _adx = context.GetIndicator($"ADX_{AdxPeriod}", q => q.GetAdx(AdxPeriod).ToList());
        _trendEma = context.GetIndicator($"EMA_{TrendEmaPeriod}", q => q.GetEma(TrendEmaPeriod).ToList());
        _channels = context.GetIndicator(
            $"Donchian_{_entryPeriod}_{_exitPeriod}",
            q => BuildChannels(q.ToList(), _entryPeriod, _exitPeriod));
    }

    public void OnBar(BarContext bar)
    {
        var i = bar.BarIndex;
        if (i < 1 || i >= _channels.Count) return;

        var price = bar.Quote.Close;
        if (price <= 0) return;

        var atr = GetAtr(i);
        var adx = GetAdx(i);
        var trendNow = _trendEma[i].Ema;
        var trendPrev = _trendEma[i - 1].Ema;
        var channel = _channels[i];

        if (atr <= 0 || adx is null || trendNow is null || trendPrev is null) return;
        if (channel.EntryHigh is null || channel.EntryLow is null || channel.ExitHigh is null || channel.ExitLow is null) return;

        var symbol = bar.Quote.Symbol;
        var longPosition = _ctx.Positions.GetPosition(symbol, PositionSide.Long);
        var shortPosition = _ctx.Positions.GetPosition(symbol, PositionSide.Short);

        // [2026-05-06 00:17 +09:00] 외부 손절/청산으로 포지션이 사라진 경우 내부 피라미딩 상태를 초기화한다.
        if (longPosition is null && shortPosition is null)
            ResetPositionState();

        // [2026-05-06 00:17 +09:00] Skender 지표 값은 double이므로 가격 계산의 기준 타입인 decimal로 맞춘다.
        var trendNowValue = (decimal)trendNow.Value;
        var trendPrevValue = (decimal)trendPrev.Value;
        var adxValue = (decimal)adx.Value;

        var longTrend = IsLongTrend(price, trendNowValue, trendPrevValue, adxValue);
        var shortTrend = IsShortTrend(price, trendNowValue, trendPrevValue, adxValue);

        if (longPosition is not null)
        {
            ManageOpenPosition(longPosition, price, atr, channel, longTrend);
            return;
        }

        if (shortPosition is not null)
        {
            ManageOpenPosition(shortPosition, price, atr, channel, shortTrend);
            return;
        }

        // [2026-05-06 00:17 +09:00] 비추세 구간은 신규 거래를 만들지 않는다.
        if (longTrend && price > channel.EntryHigh.Value)
        {
            OpenInitialUnit(symbol, PositionSide.Long, price, atr, bar.AccountEquity);
        }
        else if (shortTrend && price < channel.EntryLow.Value)
        {
            OpenInitialUnit(symbol, PositionSide.Short, price, atr, bar.AccountEquity);
        }
    }

    public IReadOnlyList<IndicatorOverlay> GetChartOverlays() =>
    [
        new($"Donchian High {_entryPeriod}", ToArray(_channels, p => p.EntryHigh), 1.2F,
            System.Drawing.Color.FromArgb(0xFF, 0x66, 0xCC, 0x99)),
        new($"Donchian Low {_entryPeriod}", ToArray(_channels, p => p.EntryLow), 1.2F,
            System.Drawing.Color.FromArgb(0xFF, 0xFF, 0x99, 0x66)),
        new($"EMA {TrendEmaPeriod}",
            _trendEma?.Select(e => e.Ema.HasValue ? (double)e.Ema.Value : double.NaN).ToArray() ?? [], 1.5F,
            System.Drawing.Color.FromArgb(0xFF, 0x88, 0xAA, 0xFF)),
    ];

    public void OnPositionOpened(FuturesPosition position)
    {
        Console.WriteLine($"  [OPEN ] {position.Side,-5} {position.Quantity:F4} @ {position.EntryPrice:N0}" +
                          $"  SL={position.StopLoss:N0}  Units={_unitCount}/{MaxUnits}");
    }

    public void OnPositionClosed(FuturesPosition position, Trade trade)
    {
        var sign = trade.NetPnL >= 0 ? "+" : "";
        Console.WriteLine($"  [CLOSE] {position.Side,-5} {trade.Quantity:F4} @ {trade.ExitPrice:N0}" +
                          $"  PnL={sign}{trade.NetPnL:F2}  ({trade.CloseReason})");
    }

    private void OpenInitialUnit(string symbol, PositionSide side, decimal price, decimal atr, decimal accountEquity)
    {
        // [2026-05-06 00:17 +09:00] 총 리스크를 4개 유닛으로 나눠 최초 진입 후 추세 확인 시 단계적으로 매집한다.
        var stopPrice = side == PositionSide.Long
            ? price - atr * AtrStopMultiple
            : price + atr * AtrStopMultiple;

        var unitRiskPercent = _riskPercent / MaxUnits;
        var unitQty = new RiskManager(_ctx.Config)
            .CalculatePositionSize(accountEquity, unitRiskPercent, price, stopPrice, _leverage);

        if (unitQty <= 0) return;

        var opened = _ctx.Positions.OpenPosition(symbol, side, unitQty, _leverage, price, OrderType.Limit);
        if (opened is null) return;

        _unitQty = unitQty;
        _unitCount = 1;
        _lastAddPrice = price;
        _isScalingOut = false;
        SetAtrStop(opened, price, atr);
    }

    private void ManageOpenPosition(FuturesPosition position, decimal price, decimal atr, ChannelPoint channel, bool trendStillValid)
    {
        // [2026-05-06 00:17 +09:00] 청산 채널 이탈 또는 추세 필터 탈락 시 수량을 한 번에 던지지 않고 DecreasePosition으로 분할 축소한다.
        var exitSignal = position.Side == PositionSide.Long
            ? price < channel.ExitLow!.Value
            : price > channel.ExitHigh!.Value;

        if (!trendStillValid || exitSignal)
        {
            DecreaseByExitRule(position, price);
            return;
        }

        TryIncreasePosition(position, price, atr);
        SetAtrStop(position, price, atr);
    }

    private void TryIncreasePosition(FuturesPosition position, decimal price, decimal atr)
    {
        if (_unitQty <= 0 || _unitCount >= MaxUnits || _isScalingOut) return;

        // [2026-05-06 00:17 +09:00] 0.5 ATR 유리하게 진행될 때마다 같은 유닛을 추가하는 터틀식 피라미딩 규칙.
        var nextAddPrice = position.Side == PositionSide.Long
            ? _lastAddPrice + atr * PyramidAtrStep
            : _lastAddPrice - atr * PyramidAtrStep;

        var canAdd = position.Side == PositionSide.Long
            ? price >= nextAddPrice
            : price <= nextAddPrice;

        if (!canAdd) return;

        var updated = _ctx.Positions.IncreasePosition(position.Symbol, position.Side, _unitQty, price, OrderType.Limit);
        if (updated is null) return;

        _unitCount++;
        _lastAddPrice = price;
        SetAtrStop(updated, price, atr);
    }

    private void DecreaseByExitRule(FuturesPosition position, decimal price)
    {
        // [2026-05-06 00:17 +09:00] 첫 분산 신호는 절반 축소, 다음 분산 신호는 잔여 수량 정리로 단순화한다.
        var reduceQty = _isScalingOut
            ? position.Quantity
            : Math.Max(position.Quantity / 2m, _unitQty);

        var reduced = _ctx.Positions.DecreasePosition(
            position.Symbol,
            position.Side,
            reduceQty,
            price,
            CloseReason.Decrease,
            OrderType.Limit);

        if (!reduced) return;

        _isScalingOut = true;
        if (!_ctx.Positions.HasPosition(position.Symbol, position.Side))
            ResetPositionState();
    }

    private void SetAtrStop(FuturesPosition position, decimal price, decimal atr)
    {
        // [2026-05-06 00:17 +09:00] ATR 추적 손절은 이익 방향으로만 이동시켜 추세의 숨 쉴 공간을 보존한다.
        var candidateStop = position.Side == PositionSide.Long
            ? price - atr * AtrStopMultiple
            : price + atr * AtrStopMultiple;

        var shouldMoveStop = position.Side == PositionSide.Long
            ? position.StopLoss <= 0 || candidateStop > position.StopLoss
            : position.StopLoss <= 0 || candidateStop < position.StopLoss;

        if (candidateStop > 0 && shouldMoveStop)
            _ctx.Positions.SetStopLoss(position.Id, candidateStop);
    }

    private static bool IsLongTrend(decimal price, decimal emaNow, decimal emaPrev, decimal adx) =>
        adx >= MinAdx && price > emaNow && emaNow > emaPrev;

    private static bool IsShortTrend(decimal price, decimal emaNow, decimal emaPrev, decimal adx) =>
        adx >= MinAdx && price < emaNow && emaNow < emaPrev;

    private decimal GetAtr(int index) =>
        _atr[index].Atr.HasValue ? (decimal)_atr[index].Atr!.Value : 0m;

    private decimal? GetAdx(int index) =>
        _adx[index].Adx.HasValue ? (decimal)_adx[index].Adx!.Value : null;

    private void ResetPositionState()
    {
        _unitQty = 0m;
        _lastAddPrice = 0m;
        _unitCount = 0;
        _isScalingOut = false;
    }

    private void NormalizePeriods()
    {
        // [2026-05-06 00:17 +09:00] 청산 채널이 진입 채널보다 길면 추세 전략의 반응성이 떨어져 기본 절반 길이로 보정한다.
        _entryPeriod = Math.Max(2, _entryPeriod);
        _exitPeriod = Math.Max(2, _exitPeriod);

        if (_exitPeriod >= _entryPeriod)
            _exitPeriod = Math.Max(2, _entryPeriod / 2);
    }

    private static IReadOnlyList<ChannelPoint> BuildChannels(IReadOnlyList<CryptoFuturesBacktester.Core.Quotes.FuturesQuote> quotes, int entryPeriod, int exitPeriod)
    {
        // [2026-05-06 00:17 +09:00] 현재 봉을 제외한 과거 채널만 계산해 미래 데이터 참조를 방지한다.
        var result = new List<ChannelPoint>(quotes.Count);
        for (var i = 0; i < quotes.Count; i++)
        {
            result.Add(new ChannelPoint(
                EntryHigh: HighestHigh(quotes, i, entryPeriod),
                EntryLow: LowestLow(quotes, i, entryPeriod),
                ExitHigh: HighestHigh(quotes, i, exitPeriod),
                ExitLow: LowestLow(quotes, i, exitPeriod)));
        }

        return result;
    }

    private static decimal? HighestHigh(IReadOnlyList<CryptoFuturesBacktester.Core.Quotes.FuturesQuote> quotes, int index, int period)
    {
        if (index < period) return null;

        var high = decimal.MinValue;
        for (var i = index - period; i < index; i++)
            high = Math.Max(high, quotes[i].High);

        return high;
    }

    private static decimal? LowestLow(IReadOnlyList<CryptoFuturesBacktester.Core.Quotes.FuturesQuote> quotes, int index, int period)
    {
        if (index < period) return null;

        var low = decimal.MaxValue;
        for (var i = index - period; i < index; i++)
            low = Math.Min(low, quotes[i].Low);

        return low;
    }

    private static double[] ToArray(IReadOnlyList<ChannelPoint>? points, Func<ChannelPoint, decimal?> selector) =>
        points?.Select(p => selector(p).HasValue ? (double)selector(p)!.Value : double.NaN).ToArray() ?? [];

    private sealed record ChannelPoint(decimal? EntryHigh, decimal? EntryLow, decimal? ExitHigh, decimal? ExitLow);
}
