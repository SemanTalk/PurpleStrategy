using PurpleStrategy.Models;

namespace PurpleStrategy.Sigmoid
{
    // [2026-05-14] HTF axis = Linear Regression Slope (정규화: Slope/Close).
    //   기존 MACD(Trend) axis 는 N=535 RT 백테스트에서 gross-edge ~0 → 폐기.
    //   신호 = sign(Slope/Close), confidence gate = RSquared ≥ MinRSquared.
    public record SigmoidSignal(int Dir, double Strength);

    public static class SigmoidHelper
    {
        // 표준 Sigmoid. SigmoidPositionManager 가 (strength × Steepness) 입력으로 호출.
        public static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        /// <summary>
        /// HTF Linear Regression Slope/Close + RSquared 로 방향/강도를 산출한다.
        /// gate: htf 존재 ∧ BBW ≥ BBWMinThreshold ∧ |Slope/Close| ≥ MinTrendMagnitude ∧ RSquared ≥ MinRSquared.
        /// strength = min(1.0, |Slope/Close| / TrendFullScale).
        /// </summary>
        public static SigmoidSignal CalcSignal(PurpleMetrics? htf, SigmoidParams p)
        {
            if (htf is null) return new SigmoidSignal(0, 0);
            if (htf.BBW < p.BBWMinThreshold) return new SigmoidSignal(0, 0);
            if (htf.Close <= 0) return new SigmoidSignal(0, 0);

            double normSlope = htf.LRResult.Slope / (double)htf.Close;
            if (Math.Abs(normSlope) < p.MinTrendMagnitude) return new SigmoidSignal(0, 0);
            if (htf.LRResult.RSquared < p.MinRSquared) return new SigmoidSignal(0, 0);

            int dir = Math.Sign(normSlope);
            if (dir == 0) return new SigmoidSignal(0, 0);

            double strength = p.TrendFullScale > 0
                ? Math.Min(1.0, Math.Abs(normSlope) / p.TrendFullScale)
                : 1.0;
            return new SigmoidSignal(dir, strength);
        }
    }
}
