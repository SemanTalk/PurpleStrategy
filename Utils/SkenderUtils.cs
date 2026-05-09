using CryptoFuturesBacktester.Core.Quotes;
using PurpleStrategy.Models;
using Skender.Stock.Indicators;
using System.Diagnostics;

namespace PurpleStrategy.Utils
{
    public static class SkenderUtils
    {
        public sealed class EmaNormalizedResult
        {
            public DateTime Date { get; set; }
            public double? ShortEma { get; set; }
            public double? LongEma { get; set; }
            public double? GapPercent { get; set; }
            public double? MaxGapPercent { get; set; }
            public double? NormalizedGap { get; set; }
        }

        public struct FairValueGap
        {
            public decimal Top;
            public decimal Bottom;
            public DateTime StartTime;
            public DateTime CreationTime; // FVG가 완성된 시간
            public DateTime EndTime;
            public bool IsBullish;
        }

        public struct OrderBlock
        {
            public decimal Top;
            public decimal Bottom;
            public DateTime StartTime;
            public DateTime EndTime;
            public bool IsBullish;
        }

        /// <summary>
        /// 차트 지표계산을 위한 기본기간값>>이값을 기준으로 계산된다.
        /// </summary>
        public static int mBasePeriod = 28;

        public static double mStdv = 2.0;
        public static int mAtrPeriod = mBasePeriod / 2;
        public static int mMfiPeriod = mBasePeriod / 2;
        public static int mShortEmaPeriod = mBasePeriod * 4;
        public static int mLongEmaPeriod = mBasePeriod * 8;

        public static int mMacdShortPeriod = mBasePeriod / 4;
        public static int mMacdLongPeriod = mBasePeriod;
        public static int mMacdSignalPeriod = mBasePeriod / 4;

        public static int mVolEmaPeriod = mBasePeriod * 4;
        public static int mRegressionLookback = mBasePeriod * 4;
        public static int mBaseLookback = 120;

        /// <summary>
        /// 퍼플 지표(수정된 %B 및 MACD 결합 알고리즘)를 계산하고 차트에 렌더링합니다.
        /// </summary>
        /// <param name="formsPlot">차트 컨트롤</param>
        /// <param name="quotes">원본 가격 데이터(OHLCV)</param>
        /// <param name="timeSpan">캔들 시간 단위</param>
        /// <param name="period">볼린저 밴드 및 EMA 기간 파라미터</param>
        /// <param name="standardDeviations">표준편차</param>
        /// <param name="mfiperiod">MFI 계산 기간</param>
        /// <returns>계산된 EMA 및 MACD 결과 세트</returns>
        public static PlotCache? RenderPurpleIndicator(
            IReadOnlyList<FuturesQuote> quotes)
        {
            try
            {
                var cache = new PlotCache();

                if (quotes == null || !quotes.Any()) return null;

                // 데이터 정렬 및 유효성 검사 (계산에 필요한 최소 데이터 확보)
                var quoteList = quotes.OrderBy(x => x.Date).ToList();
                if (quoteList.Count < mBasePeriod * 2) return null;

                // --------------------------------------------------------------------
                // 1. 가격 기반 %B 계산 (%B = (Price - Lower) / (Upper - Lower))
                // --------------------------------------------------------------------
                var bbResults = quoteList.GetBollingerBands(mBasePeriod, mStdv);
                var percentBQuotes = bbResults
                    .Where(x => x.PercentB.HasValue)
                    .Select(x => new Quote { Date = x.Date, Close = (decimal)x.PercentB!.Value })
                    .ToList();

                if (percentBQuotes.Count < 3) return null;
                var sPbs = percentBQuotes.GetEma(3).ToList(); //  스무딩

                // --------------------------------------------------------------------
                // 2. MFI(Money Flow Index) 기반 %B 계산 (자금 흐름의 강도 측정)
                // --------------------------------------------------------------------
                var mfiResults = quoteList.GetMfi(mMfiPeriod).ToList();
                var mfiQuotes = mfiResults
                    .Where(x => x.Mfi.HasValue)
                    .Select(x => new Quote { Date = x.Date, Close = (decimal)x.Mfi!.Value })
                    .ToList();

                var mfiBbResults = mfiQuotes.GetBollingerBands(mBasePeriod, mStdv);
                var percentMQuotes = mfiBbResults
                    .Where(x => x.PercentB.HasValue)
                    .Select(x => new Quote { Date = x.Date, Close = (decimal)x.PercentB!.Value })
                    .ToList();

                if (percentMQuotes.Count < 3) return null;
                var sMbs = percentMQuotes.GetEma(3).ToList(); //  스무딩

                // --------------------------------------------------------------------
                // 3. 퍼플 MACD 계산 (단기 %B와 장기 %B의 수렴/확산 측정)
                // --------------------------------------------------------------------
                var shortEma = percentBQuotes.GetEma(mMacdShortPeriod).ToList();
                var longEma = percentMQuotes.GetEma(mMacdLongPeriod).ToList();

                // 날짜 기준 Join을 통해 데이터 일관성 유지
                var macdBasicQuotes = shortEma
                    .Join(longEma, f => f.Date, s => s.Date, (f, s) => new Quote
                    {
                        Date = f.Date,
                        Close = (decimal)((f.Ema ?? 0) - (s.Ema ?? 0))
                    }).ToList();

                var signalEma = macdBasicQuotes.GetEma(mMacdSignalPeriod).ToList();
                var macdResults = macdBasicQuotes
                    .Join(signalEma, m => m.Date, s => s.Date, (m, s) => new MacdResult(m.Date)
                    {
                        Macd = (double)m.Close,
                        Signal = (double?)s.Ema,
                        Histogram = (double)m.Close - (double)(s.Ema ?? 0)
                    }).ToList();

                if (!macdResults.Any()) return null;

                cache.mSPbResults = sPbs;
                cache.mSMbResults = sMbs;
                cache.mMacdResuts = macdResults;
                cache.mQuoteResults = quoteList;

                return cache;// (sPbs, sMbs, macdResults);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RenderPurpleIndicator Critical Error: {ex}");
                return null;
            }
        }

        public static PlotCache? RenderBollingerBands(IReadOnlyList<FuturesQuote> quotes)
        {
            if (quotes == null) return null;

            try
            {
                var cache = new PlotCache();

                // 2. Skender 지표 계산 및 유효성 검사
                var allResults = quotes.GetBollingerBands(mBasePeriod, mStdv).ToList();
                var validResults = allResults
                    .Where(x => x.UpperBand != null && x.LowerBand != null && x.Sma != null)
                    .ToList();

                if (validResults.Count == 0) return null;

                // 3. 밴드폭(Bandwidth) 정규화 및 데이터 배열 준비
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
                    int startIndex = Math.Max(0, i - mBaseLookback + 1);
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

                cache.mQuoteResults = quotes.ToList();
                cache.mBollingerBandsResults = allResults;

                return cache;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bollinger Render Error: {ex.Message}");
                return null;
            }
        }

        public static PlotCache? RenderDualEma(IReadOnlyList<FuturesQuote> quotes)
        {
            // 안전성: 폼/데이터 체크
            var quoteList = quotes.OrderBy(x => x.Date).ToList();
            if (quoteList.Count < mLongEmaPeriod) return null;

            try
            {
                var cache = new PlotCache();
                // EMA 계산
                var emaShort = quoteList.GetEma(mShortEmaPeriod);
                var emaLong = quoteList.GetEma(mLongEmaPeriod);

                var validData = quoteList
                    .Select((q, i) => new
                    {
                        Index = i,
                        Quote = q,
                        EmaS = emaShort.ElementAt(i),
                        EmaL = emaLong.ElementAt(i)
                    })
                    .Where(x => x.EmaL.Ema != null)
                    .ToList();

                if (validData.Count == 0) return null;

                double[] xs = validData.Select(x => x.Quote.Date.ToOADate()).ToArray();
                double[] ysShort = validData.Select(x => x.EmaS.Ema.Value).ToArray();
                double[] ysLong = validData.Select(x => x.EmaL.Ema.Value).ToArray();

                cache.mShortEmaResults = emaShort.ToList();
                cache.mLongEmaResults = emaLong.ToList();
                cache.mQuoteResults = quotes.ToList();
                return cache;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DualEma Render Error: {ex.Message}");
                return null;
            }
        }

        public static PlotCache? RenderVolume(IReadOnlyList<FuturesQuote> quotes)
        {
            var quoteList = quotes.OrderBy(x => x.Date).ToList();
            if (quoteList.Count == 0) return null;

            try
            {
                var cache = new PlotCache();
                var volumes = quoteList.Select(q => (double)q.Volume).ToArray();
                var dates = quoteList.Select(q => q.Date.ToOADate()).ToArray();

                var volumeEmaResults = quotes
                    .Use(CandlePart.Volume) // 계산 대상을 거래량으로 지정
                    .GetEma(mVolEmaPeriod);

                cache.mVolEmaResults = volumeEmaResults.ToList();
                cache.mQuoteResults = quotes.ToList();
                return cache;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Volume Render Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Swing High 또는 Swing Low 지점들을 찾아 DateTime과 Value 쌍으로 반환합니다.
        /// </summary>
        public static List<(DateTime DateTime, double Value)> GetSwingPoints(IReadOnlyList<FuturesQuote> data, bool findHigh, int strength)
        {
            var points = new List<(DateTime DateTime, double Value)>();
            for (int i = strength; i < data.Count - strength; i++)
            {
                bool isSwing = true;
                double targetValue = findHigh ? (double)data[i].High : (double)data[i].Low;

                for (int j = 1; j <= strength; j++)
                {
                    double prevValue = findHigh ? (double)data[i - j].High : (double)data[i - j].Low;
                    double nextValue = findHigh ? (double)data[i + j].High : (double)data[i + j].Low;

                    if (findHigh)
                    {
                        if (targetValue < prevValue || targetValue < nextValue) { isSwing = false; break; }
                    }
                    else
                    {
                        if (targetValue > prevValue || targetValue > nextValue) { isSwing = false; break; }
                    }
                }

                if (isSwing) points.Add((data[i].Date, targetValue));
            }
            return points;
        }

        /// <summary>
        /// 최소자승법(Ordinary Least Squares)을 이용한 선형 회귀 계산
        /// </summary>
        public static RegressionResult CalculateLinearRegression(IReadOnlyList<FuturesQuote> quotes)
        {
            int n = quotes.Count;
            if (n < 2) return new RegressionResult();

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;

            for (int i = 0; i < n; i++)
            {
                // ElementAt 대신 인덱서 사용, 형변환 최적화
                double y = (double)quotes[i].Close;
                sumX += i;
                sumY += y;
                sumXY += i * y;
                sumX2 += (double)i * i;
                sumY2 += y * y; // R-Squared와 StdDev 계산에 재사용 가능
            }
            // 기울기 및 절편 계산
            double denominator = (n * sumX2 - sumX * sumX);
            if (Math.Abs(denominator) < double.Epsilon) return new RegressionResult(); // 분모 0 방지

            double slope = (n * sumXY - sumX * sumY) / denominator;
            double intercept = (sumY - slope * sumX) / n;

            // --- 단일 루프로 StdDev와 R2 계산하기 ---
            // SSE (잔차 제곱합): sumY2 - intercept * sumY - slope * sumXY
            double sse = Math.Max(0, sumY2 - (intercept * sumY) - (slope * sumXY));
            double stdDev = Math.Sqrt(sse / n);

            // SST (전체 변동성): sumY2 - (sumY * sumY / n)
            double sst = sumY2 - (sumY * sumY / n);
            double rSquared = (sst > 0) ? 1 - (sse / sst) : 0;

            return new RegressionResult
            {
                Slope = slope,
                Intercept = intercept,
                StdDev = stdDev,
                RSquared = rSquared,
                CurrentBasis = slope * (n - 1) + intercept
            };
        }

        /// <summary>
        /// FVG 분석: 갭의 크기가 해당 시점 ATR의 배수 이상인 경우에만 추출합니다.
        /// </summary>
        public static List<FairValueGap> AnalyzeFVG(IReadOnlyList<FuturesQuote> candles, IReadOnlyList<AtrResult> atrResults, double gapAtrMultiplier)
        {
            var atrDict = atrResults.Where(x => x.Atr.HasValue).ToDictionary(x => x.Date, x => x.Atr!.Value);
            var results = new List<FairValueGap>();
            for (int i = 2; i < candles.Count; i++)
            {
                var c1 = candles[i - 2];
                var c3 = candles[i];

                // 해당 시점의 ATR 값 가져오기
                if (!atrDict.TryGetValue(c3.Date, out double currentAtr)) continue;
                if (currentAtr == 0) continue;

                decimal minGapRequired = (decimal)(currentAtr * gapAtrMultiplier);

                DateTime last = candles.Last().Date;
                DateTime lastCandleTime = last.Date;
                // Bullish FVG
                decimal gapUp = c3.Low - c1.High;
                if (gapUp >= minGapRequired)
                {
                    results.Add(new FairValueGap
                    {
                        Top = c3.Low,
                        Bottom = c1.High,
                        StartTime = c1.Date,
                        CreationTime = c3.Date,
                        EndTime = lastCandleTime,
                        IsBullish = true
                    });
                }
                // Bearish FVG
                decimal gapDown = c1.Low - c3.High;
                if (gapDown >= minGapRequired)
                {
                    results.Add(new FairValueGap
                    {
                        Top = c1.Low,
                        Bottom = c3.High,
                        StartTime = c1.Date,
                        CreationTime = c3.Date,
                        EndTime = lastCandleTime,
                        IsBullish = false
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Order Block 분석: 캔들 몸통 크기가 해당 시점 ATR의 배수 이상인 경우에만 추출합니다.
        /// </summary>
        public static List<OrderBlock> AnalyzeOrderBlocks(IReadOnlyList<FuturesQuote> candles, IReadOnlyList<AtrResult> atrResults, double bodyAtrMultiplier)
        {
            var atrDict = atrResults.Where(x => x.Atr.HasValue).ToDictionary(x => x.Date, x => x.Atr!.Value);
            var results = new List<OrderBlock>();
            for (int i = 0; i < candles.Count - 1; i++)
            {
                var current = candles[i];
                var next = candles[i + 1];

                if (!atrDict.TryGetValue(current.Date, out double currentAtr)) continue;
                if (currentAtr == 0) continue;

                decimal bodySize = Math.Abs(current.Open - current.Close);
                decimal minBodyRequired = (decimal)(currentAtr * bodyAtrMultiplier);

                DateTime last = candles.Last().Date;
                DateTime lastCandleTime = last.Date;
                // 조건: 몸통 크기가 ATR 배수 기준 이상이고, 다음 캔들이 돌파 발생
                if (bodySize >= minBodyRequired)
                {
                    if (current.Close < current.Open && next.Close > current.High) // Bullish OB
                    {
                        results.Add(new OrderBlock
                        {
                            Top = current.High,
                            Bottom = current.Low,
                            StartTime = current.Date,
                            EndTime = lastCandleTime,
                            IsBullish = true
                        });
                    }
                    else if (current.Close > current.Open && next.Close < current.Low) // Bearish OB
                    {
                        results.Add(new OrderBlock
                        {
                            Top = current.High,
                            Bottom = current.Low,
                            StartTime = current.Date,
                            EndTime = lastCandleTime,
                            IsBullish = false
                        });
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// 가격이 OB 범위를 관통했는지 확인하여 유효한 것만 반환합니다.
        /// </summary>
        public static List<OrderBlock> FilterMitigatedOB(IReadOnlyList<OrderBlock> obs, IReadOnlyList<FuturesQuote> candles)
        {
            var validObs = new List<OrderBlock>();
            foreach (var ob in obs)
            {
                bool isMitigated = false;
                var postCandles = candles.Where(c => c.Date > ob.StartTime);

                foreach (var candle in postCandles)
                {
                    if (ob.IsBullish)
                    {
                        if (candle.Low < ob.Bottom) { isMitigated = true; break; }
                    }
                    else
                    {
                        if (candle.High > ob.Top) { isMitigated = true; break; }
                    }
                }
                if (!isMitigated) validObs.Add(ob);
            }
            return validObs;
        }

        /// <summary>
        /// 가격이 FVG 범위를 관통했는지 확인하여 유효한 것만 반환합니다.
        /// </summary>
        public static List<FairValueGap> FilterMitigatedFVG(IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<FuturesQuote> candles)
        {
            var validFvgs = new List<FairValueGap>();
            foreach (var fvg in fvgs)
            {
                bool isMitigated = false;
                // FVG 생성 시점 이후의 캔들들을 확인
                var postCandles = candles.Where(c => c.Date > fvg.CreationTime);

                foreach (var candle in postCandles)
                {
                    if (fvg.IsBullish)
                    {
                        if (candle.Low < fvg.Bottom) { isMitigated = true; break; }
                    }
                    else
                    {
                        if (candle.High > fvg.Top) { isMitigated = true; break; }
                    }
                }
                if (!isMitigated) validFvgs.Add(fvg);
            }
            return validFvgs;
        }
    }
}