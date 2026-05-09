using CryptoFuturesBacktester.Core.Quotes;
using PurpleStrategy.Models;
using Skender.Stock.Indicators;
using System.Diagnostics;

namespace PurpleStrategy.Utils
{
    public static class PurpleMetricsUtils
    {
        // ============================================================================
        // IndicatorSnapshot: 특정 시점의 모든 지표를 하나의 객체로 묶는 DTO
        // ============================================================================
        public class IndicatorSnapshot
        {
            public BollingerBandsResult BB { get; init; } = null!;
            public EmaResult ShortEma { get; init; } = null!;
            public EmaResult LongEma { get; init; } = null!;
            public EmaResult VolEma { get; init; } = null!;
            public EmaResult SPb { get; init; } = null!;
            public EmaResult SMb { get; init; } = null!;
            public MacdResult Macd { get; init; } = null!;
        }

        // ============================================================================
        // IndicatorLookup: 날짜 → 지표 O(1) 조회용 Dictionary 모음
        // ============================================================================
        public class IndicatorLookup
        {
            public Dictionary<DateTime, BollingerBandsResult> BB { get; init; } = null!;
            public Dictionary<DateTime, EmaResult> ShortEma { get; init; } = null!;
            public Dictionary<DateTime, EmaResult> LongEma { get; init; } = null!;
            public Dictionary<DateTime, EmaResult> VolEma { get; init; } = null!;
            public Dictionary<DateTime, EmaResult> SPb { get; init; } = null!;
            public Dictionary<DateTime, EmaResult> SMb { get; init; } = null!;
            public Dictionary<DateTime, MacdResult> Macd { get; init; } = null!;

            /// <summary>
            /// 지정 날짜의 모든 지표를 한 번에 조회. 하나라도 없으면 null 반환.
            /// </summary>
            public IndicatorSnapshot? GetSnapshot(DateTime date)
            {
                if (!BB.TryGetValue(date, out var bb)) return null;
                if (!ShortEma.TryGetValue(date, out var sEma)) return null;
                if (!LongEma.TryGetValue(date, out var lEma)) return null;
                if (!VolEma.TryGetValue(date, out var vEma)) return null;
                if (!SPb.TryGetValue(date, out var spbS)) return null;
                if (!SMb.TryGetValue(date, out var smbS)) return null;
                if (!Macd.TryGetValue(date, out var macdS)) return null;

                return new IndicatorSnapshot
                {
                    BB = bb,
                    ShortEma = sEma,
                    LongEma = lEma,
                    VolEma = vEma,
                    SPb = spbS,
                    SMb = smbS,
                    Macd = macdS,
                };
            }

            // ============================================================================
            // IndicatorLookup 생성 (quotes 직접 계산 — FormsPlot 없이, SQLite 백테스트용)
            // ============================================================================
            public static IndicatorLookup? BuildIndicatorLookupFromQuotes(IReadOnlyList<FuturesQuote> quotes)
            {
                try
                {
                    var quoteList = quotes.OrderBy(x => x.Date).ToList();

                    // BB(28, 2.0) — price
                    var bbResults = quoteList.GetBollingerBands(SkenderUtils.mBasePeriod, SkenderUtils.mStdv).ToList();
                    var validResults = bbResults.Where(x => x.UpperBand != null && x.LowerBand != null && x.Sma != null).ToList();

                    if (validResults.Count == 0) return null;

                    // 밴드폭(Bandwidth) 정규화 및 데이터 배열 준비>>validResults 사용
                    int count = validResults.Count;
                    double[] xs = new double[count];
                    double[] uppers = new double[count];
                    double[] lowers = new double[count];
                    double[] smas = new double[count];
                    double[] normalizedWidths = new double[count];

                    for (int i = 0; i < count; i++)
                    {
                        var res = validResults[i];

                        xs[i] = res.Date.ToOADate();
                        uppers[i] = res.UpperBand!.Value;
                        lowers[i] = res.LowerBand!.Value;
                        smas[i] = res.Sma!.Value;

                        // 현재 밴드폭 계산
                        double currentWidth = (uppers[i] - lowers[i]) / smas[i];

                        // 최근 120기간 내 최대 밴드폭 계산 (최적화된 탐색)
                        int startIndex = Math.Max(0, i - SkenderUtils.mBaseLookback + 1);
                        double maxWidthInRange = 0;
                        for (int j = startIndex; j <= i; j++)
                        {
                            var r = validResults[j];
                            double w = (r.UpperBand!.Value - r.LowerBand!.Value) / r.Sma!.Value;
                            if (w > maxWidthInRange) maxWidthInRange = w;
                        }

                        // 정규화 점수 (0~100)
                        res.Width = normalizedWidths[i] = maxWidthInRange > 0 ? (currentWidth / maxWidthInRange) * 100 : 0;
                    }

                    // Price EMA(14), EMA(112)
                    var shortEma = quoteList.GetEma(SkenderUtils.mShortEmaPeriod).ToList();
                    var longEma = quoteList.GetEma(SkenderUtils.mLongEmaPeriod).ToList();

                    // Volume EMA(112)
                    var volEma = quotes.Use(CandlePart.Volume).GetEma(SkenderUtils.mVolEmaPeriod).ToList();

                    // SPb: BB(28,2.0).PercentB → EMA(3)
                    var percentBQuotes = bbResults
                        .Where(x => x.PercentB.HasValue)
                        .Select(x => new Quote { Date = x.Date, Close = (decimal)x.PercentB!.Value })
                        .ToList();
                    var sPbs = percentBQuotes.GetEma(3).ToList();

                    // SMb: MFI(14) → BB(56, 2.1).PercentB → EMA(3)
                    var mfiQuotes = quoteList.GetMfi(SkenderUtils.mMfiPeriod)
                        .Where(x => x.Mfi.HasValue)
                        .Select(x => new Quote { Date = x.Date, Close = (decimal)x.Mfi!.Value })
                        .ToList();
                    var percentMQuotes = mfiQuotes.GetBollingerBands(SkenderUtils.mBasePeriod, SkenderUtils.mStdv)
                        .Where(x => x.PercentB.HasValue)
                        .Select(x => new Quote { Date = x.Date, Close = (decimal)x.PercentB!.Value })
                        .ToList();
                    var sMbs = percentMQuotes.GetEma(3).ToList();

                    // Purple MACD: shortEma(percentB, 7) - longEma(percentM, 28) → signal EMA(7)
                    var shortPurple = percentBQuotes.GetEma(SkenderUtils.mMacdShortPeriod).ToList();
                    var longPurple = percentMQuotes.GetEma(SkenderUtils.mMacdLongPeriod).ToList();
                    var macdBasic = shortPurple
                        .Join(longPurple, f => f.Date, s => s.Date, (f, s) => new Quote
                        {
                            Date = f.Date,
                            Close = (decimal)((f.Ema ?? 0) - (s.Ema ?? 0))
                        }).ToList();
                    var signalEma = macdBasic.GetEma(SkenderUtils.mMacdSignalPeriod).ToList();
                    var macdResults = macdBasic
                        .Join(signalEma, m => m.Date, s => s.Date, (m, s) => new MacdResult(m.Date)
                        {
                            Macd = (double)m.Close,
                            Signal = (double?)s.Ema,
                            Histogram = (double)m.Close - (double)(s.Ema ?? 0)
                        }).ToList();

                    return new IndicatorLookup
                    {
                        BB = bbResults.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        ShortEma = shortEma.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        LongEma = longEma.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        VolEma = volEma.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        SPb = sPbs.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        SMb = sMbs.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                        Macd = macdResults.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"{ex.Message}-{ex.StackTrace}");
                    return null;
                }
            }

            // ============================================================================
            // IndicatorLookup 생성 (PlotCache → Dictionary 변환)
            // ============================================================================
            public static IndicatorLookup BuildIndicatorLookup(
                PlotCache? bbCache,
                PlotCache? emaCache,
                PlotCache? volCache,
                PlotCache? purpleCache)
            {
                // ToDictionary can throw on duplicate Date keys. GroupBy+Last() picks the last entry for each date
                // which is safer when the source lists may contain duplicates.
                return new IndicatorLookup
                {
                    BB = bbCache!.mBollingerBandsResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    ShortEma = emaCache!.mShortEmaResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    LongEma = emaCache!.mLongEmaResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    VolEma = volCache!.mVolEmaResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    SPb = purpleCache!.mSPbResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    SMb = purpleCache!.mSMbResults!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                    Macd = purpleCache!.mMacdResuts!.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Last()),
                };
            }

            // ============================================================================
            // ATR을 인덱스 기준으로 미리 계산 (룩어헤드 바이어스 제거)
            // ============================================================================
            public static Dictionary<int, decimal> PrecomputeAtr(
                IReadOnlyList<FuturesQuote> quotes, int atrPeriod, int warmup)
            {
                var result = new Dictionary<int, decimal>();

                // 전체 리스트에서 ATR을 한 번에 계산
                var allAtr = quotes.GetAtr(atrPeriod).ToList();

                for (int i = 0; i < allAtr.Count; i++)
                {
                    if (allAtr[i].Atr != null)
                    {
                        result[i] = (decimal)allAtr[i].Atr!;
                    }
                }

                return result;
            }

            // ============================================================================
            // PurpleMetrics 조립
            // ============================================================================
            public static PurpleMetrics BuildMetrics(
                FuturesQuote candle, FuturesQuote prevCandle, FuturesQuote pprevCandle,
                IndicatorSnapshot current, IndicatorSnapshot prev, IndicatorSnapshot pprev,
                decimal atrValue,
                RegressionResult rResult,
                RegressionResult prevRResult,
                RegressionResult pprevRResult)
            {
                return new PurpleMetrics
                {
                    // --- ATR ---
                    ATR = (double)atrValue,

                    // --- 선형회귀 ---
                    LRResult = rResult,
                    PrevLRResult = prevRResult,
                    PPrevLRResult = pprevRResult,
                    // --- 현재 확정 봉 (prevCandle) ---
                    Date = candle.Date,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    Volume = candle.Volume,

                    BBC = current.BB.Sma ?? 0,
                    BBUpper = current.BB.UpperBand ?? 0,
                    BBLower = current.BB.LowerBand ?? 0,
                    BBW = current.BB.Width ?? 0,
                    ShortEma = current.ShortEma.Ema ?? 0,
                    LongEma = current.LongEma.Ema ?? 0,
                    VolEma = current.VolEma.Ema ?? 0,
                    SPb = current.SPb.Ema ?? 0,
                    SMb = current.SMb.Ema ?? 0,
                    Trend = current.Macd.Macd ?? 0,
                    Histogram = current.Macd.Histogram ?? 0,

                    // --- 1봉 전 (pprevCandle) ---
                    PrevOpen = prevCandle.Open,
                    PrevHigh = prevCandle.High,
                    PrevLow = prevCandle.Low,
                    PrevClose = prevCandle.Close,
                    PrevVolume = prevCandle.Volume,

                    PrevBBC = prev.BB.Sma ?? 0,
                    PrevBBUpper = prev.BB.UpperBand ?? 0,
                    PrevBBLower = prev.BB.LowerBand ?? 0,
                    PrevBBW = prev.BB.Width ?? 0,
                    PrevShortEma = prev.ShortEma.Ema ?? 0,
                    PrevLongEma = prev.LongEma.Ema ?? 0,
                    PrevVolEma = prev.VolEma.Ema ?? 0,
                    PrevSPb = prev.SPb.Ema ?? 0,
                    PrevSMb = prev.SMb.Ema ?? 0,
                    PrevTrend = prev.Macd.Macd ?? 0,
                    PrevHistogram = prev.Macd.Histogram ?? 0,

                    // --- 2봉 전 (ppprevCandle) ---
                    PPrevOpen = pprevCandle.Open,
                    PPrevHigh = pprevCandle.High,
                    PPrevLow = pprevCandle.Low,
                    PPrevClose = pprevCandle.Close,
                    PPrevVolume = pprevCandle.Volume,

                    PPrevBBC = pprev.BB.Sma ?? 0,
                    PPrevBBUpper = pprev.BB.UpperBand ?? 0,
                    PPrevBBLower = pprev.BB.LowerBand ?? 0,
                    PPrevBBW = pprev.BB.Width ?? 0,
                    PPrevShortEma = pprev.ShortEma.Ema ?? 0,
                    PPrevLongEma = pprev.LongEma.Ema ?? 0,
                    PPrevVolEma = pprev.VolEma.Ema ?? 0,
                    PPrevSPb = pprev.SPb.Ema ?? 0,
                    PPrevSMb = pprev.SMb.Ema ?? 0,
                    PPrevTrend = pprev.Macd.Macd ?? 0,
                    PPrevHistogram = pprev.Macd.Histogram ?? 0,
                };
            }

            public static IReadOnlyList<PurpleMetrics> BuildMetricsList(
                IReadOnlyList<FuturesQuote> quotes, IndicatorLookup? lookup, int regressionLookback)
            {
                var result = new List<PurpleMetrics>(quotes.Count);
                var atrByIndex = PrecomputeAtr(quotes, SkenderUtils.mBasePeriod / 2, SkenderUtils.mBasePeriod);
                if (lookup == null) return result;

                for (int i = 3; i < quotes.Count; i++)
                {
                    var current = lookup.GetSnapshot(quotes[i].Date);
                    var prev = lookup.GetSnapshot(quotes[i - 1].Date);
                    var pprev = lookup.GetSnapshot(quotes[i - 2].Date);
                    if (current == null || prev == null || pprev == null) continue;

                    atrByIndex.TryGetValue(i, out var atr);
                    result.Add(BuildMetrics(
                        quotes[i], quotes[i - 1], quotes[i - 2],
                        current, prev, pprev, atr,
                        CalculateRegressionAtIndex(quotes, i, regressionLookback),
                        CalculateRegressionAtIndex(quotes, i - 1, regressionLookback),
                        CalculateRegressionAtIndex(quotes, i - 2, regressionLookback)));
                }

                return result;
            }

            // ============================================================================
            // 선형회귀를 인덱스 기준으로 계산
            // ============================================================================
            public static RegressionResult CalculateRegressionAtIndex(
                IReadOnlyList<FuturesQuote> quotes, int currentIndex, int lookback)
            {
                int windowSize = Math.Min(currentIndex + 1, lookback);
                int startIdx = currentIndex - windowSize + 1;
                var subList = quotes.Skip(startIdx).Take(windowSize).ToList();
                return SkenderUtils.CalculateLinearRegression(subList);
            }

            public static DateTime GetBucketKey(DateTime dt, int intervalMinutes)
            {
                // 초/밀리초를 포함한 전체 시간을 분 단위로 변환하여 계산
                // dt.Ticks를 이용하면 훨씬 정확한 계산이 가능합니다.
                long intervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks;
                long snappedTicks = (dt.Ticks / intervalTicks) * intervalTicks;
                return new DateTime(snappedTicks, dt.Kind);
            }
        }
    }
}