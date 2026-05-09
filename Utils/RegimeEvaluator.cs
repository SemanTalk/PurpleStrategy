using PurpleStrategy.Models;

namespace PurpleStrategy.Utils

{
    public enum MarketRegime
    {
        TrendLong,
        TrendShort,
        MeanReversionLong,
        MeanReversionShort,
        Neutral
    }

    public record RegimeResult(
        MarketRegime Regime,
        double Confidence,
        string Reason
    );

    /// <summary>
    /// LR 채널 기반 레짐 평가 임계값
    /// </summary>
    public record RegimeThresholds
    {
        public double SlopeThreshold { get; init; } = 0.001;  // |Slope| >= 이면 추세
        public double R2Min { get; init; } = 0.55;   // R² 최소값 (LR 신뢰도)
        public double TrendMin { get; init; } = 0.03;   // |Purple MACD| >= 이면 추세 확인
        public double SPbOverbought { get; init; } = 0.80;   // s-%b 과매수
        public double SPbOversold { get; init; } = 0.20;   // s-%b 과매도
        public double SMbOverbought { get; init; } = 0.80;   // s-%m 과매수
        public double SMbOversold { get; init; } = 0.20;   // s-%m 과매도
    }

    /// <summary>
    /// LR 채널을 주신호로, SPb/SMb/Trend/Histogram을 확인신호로 사용하는 레짐 평가기.
    /// Score 의존 없음.
    /// </summary>
    public static class RegimeEvaluator
    {
        private static readonly RegimeThresholds Default = new();

        public static RegimeResult Evaluate(
            PurpleMetrics? m,

            RegimeThresholds? thresholds = null)
        {
            var t = thresholds ?? Default;
            if(m==null) return new(MarketRegime.Neutral, 0.0, $"PurpleMetrics is null");
            double slope = m.LRResult.Slope;
            double r2 = m.LRResult.RSquared;
            double basis = m.LRResult.CurrentBasis;
            double stdDev = m.LRResult.StdDev;
            double upper = basis + 2 * stdDev;
            double lower = basis - 2 * stdDev;
            double close = (double)m.Close;

            double spb = m.SPb;
            double smb = m.SMb;
            double trend = m.Trend;
            double hist = m.Histogram;
            double prevHist = m.PrevHistogram;

            // ── LR channel primary ──────────────────────────────────────
            bool lrValid = r2 >= t.R2Min;
            bool lrBull = lrValid && slope > t.SlopeThreshold;
            bool lrBear = lrValid && slope < -t.SlopeThreshold;

            bool priceAboveBasis = close > basis;
            bool priceBelowBasis = close < basis;
            bool priceAtUpper = close >= upper * 0.99;  // within 1% of upper band
            bool priceAtLower = close <= lower * 1.01;  // within 1% of lower band

            // ── Confirmation signals ────────────────────────────────────
            bool trendBull = trend > t.TrendMin;
            bool trendBear = trend < -t.TrendMin;
            bool histBull = hist > 0 && hist >= prevHist;   // rising positive momentum
            bool histBear = hist < 0 && hist <= prevHist;   // falling negative momentum
            bool histRevUp = hist > prevHist;                 // histogram turning up
            bool histRevDn = hist < prevHist;                 // histogram turning down

            bool spbOB = spb > t.SPbOverbought;
            bool spbOS = spb < t.SPbOversold;
            bool smbOB = smb > t.SMbOverbought;
            bool smbOS = smb < t.SMbOversold;

            // ═══════════════════════════════════════════════════════════
            //  TrendLong: LR upslope + price above basis + MACD + momentum
            // ═══════════════════════════════════════════════════════════
            if (lrBull && priceAboveBasis && trendBull && histBull)
            {
                int score = 0;
                if (lrBull) score++;
                if (trendBull) score++;
                if (histBull) score++;
                if (spb > 0.40 && spb < 0.80) score++;
                return new(MarketRegime.TrendLong, score / 4.0,
                    $"LR↑ slope={slope:F4} r²={r2:F2} Trend={trend:F3} Hist={hist:F3} SPb={spb:F2}");
            }

            // ═══════════════════════════════════════════════════════════
            //  TrendShort: LR downslope + price below basis + MACD + momentum
            // ═══════════════════════════════════════════════════════════
            if (lrBear && priceBelowBasis && trendBear && histBear)
            {
                int score = 0;
                if (lrBear) score++;
                if (trendBear) score++;
                if (histBear) score++;
                if (spb > 0.20 && spb < 0.60) score++;
                return new(MarketRegime.TrendShort, score / 4.0,
                    $"LR↓ slope={slope:F4} r²={r2:F2} Trend={trend:F3} Hist={hist:F3} SPb={spb:F2}");
            }

            // ═══════════════════════════════════════════════════════════
            //  MeanReversionLong: oversold extreme + histogram reversal
            // ═══════════════════════════════════════════════════════════
            if ((priceAtLower || spbOS) && histRevUp && !trendBear)
            {
                int score = 0;
                if (spbOS) score++;
                if (smbOS) score++;
                if (histRevUp) score++;
                if (priceAtLower) score++;
                return new(MarketRegime.MeanReversionLong, score / 4.0,
                    $"MRL SPb={spb:F2}(OS) SMb={smb:F2} Hist↑ lower={lower:F1}");
            }

            // ═══════════════════════════════════════════════════════════
            //  MeanReversionShort: overbought extreme + histogram reversal
            // ═══════════════════════════════════════════════════════════
            if ((priceAtUpper || spbOB) && histRevDn && !trendBull)
            {
                int score = 0;
                if (spbOB) score++;
                if (smbOB) score++;
                if (histRevDn) score++;
                if (priceAtUpper) score++;
                return new(MarketRegime.MeanReversionShort, score / 4.0,
                    $"MRS SPb={spb:F2}(OB) SMb={smb:F2} Hist↓ upper={upper:F1}");
            }

            // ═══════════════════════════════════════════════════════════
            //  Weak Trend (LR valid + MACD direction, no histogram confirm)
            // ═══════════════════════════════════════════════════════════
            if (lrBull && trendBull)
                return new(MarketRegime.TrendLong, 0.25,
                    $"약추세Long slope={slope:F4} r²={r2:F2} Trend={trend:F3}");

            if (lrBear && trendBear)
                return new(MarketRegime.TrendShort, 0.25,
                    $"약추세Short slope={slope:F4} r²={r2:F2} Trend={trend:F3}");

            // ═══════════════════════════════════════════════════════════
            //  Neutral
            // ═══════════════════════════════════════════════════════════
            return new(MarketRegime.Neutral, 0.0,
                $"slope={slope:F4} r²={r2:F2} Trend={trend:F3} SPb={spb:F2} 명확한 신호 없음");
        }
    }
}