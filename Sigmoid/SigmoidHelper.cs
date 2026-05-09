using PurpleStrategy.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PurpleStrategy.Sigmoid
{
    public class SigmoidHelper
    {
        // 표준 Sigmoid 함수. 입력이 커질수록 1에 가까워지고 작아질수록 0에 가까워진다.
        // 현재 사용처에서는 결과를 activeMaxWeight로 잘라 목표 비중으로 쓴다.
        public static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        /// <summary>
        /// LTF/HTF 지표에서 진입 방향을 계산한다.
        /// htfMetrics가 있으면 HTF의 Trend/EMA/BBW만으로 방향을 정하고, 0을 반환하여 전략을 중지한다..
        /// 반환값: 1=Long, -1=Short, 0=None.
        /// </summary>
        public static int CalcDir1(PurpleMetrics ltf, PurpleMetrics? htf, SigmoidParams Params, double finalScore)
        {
            // Long Positive
            // 1. htf.SPb > htf.SMb
            // 2. htf.Trend > Params.MinTrendMagnitude

            int dir = 0;
            if (htf == null)
            {
                if (ltf.BBW < Params.BBWMinThreshold) { dir = 0; }
                else if (ltf.Trend > Params.MinTrendMagnitude) { dir = 1; }
                else if (ltf.Trend < -Params.MinTrendMagnitude) { dir = -1; }
                return dir;
            }

            //bool ltfOverBuy = ltf.SMb > 0.8 && ltf.SPb > 0.8;
            //bool ltfOverSell = ltf.SMb < 0.2 && ltf.SPb < 0.2;

            if (htf.BBW < Params.BBWMinThreshold) { dir = 0; }
            else if (htf.Trend > Params.MinTrendMagnitude) { dir = 1; }
            else if (htf.Trend < -Params.MinTrendMagnitude ) { dir = -1; }

            return dir;
        }

        /// <summary>
        /// LTF/HTF 선형회귀 점수에서 진입 방향을 계산한다.
        /// 회귀 점수가 양수 임계값을 넘으면 Long, 음수 임계값 아래면 Short, 그 사이는 None이다.
        /// 반환값: 1=Long, -1=Short, 0=None.
        /// </summary>
        public static int CalcDir2(PurpleMetrics ltf, PurpleMetrics? htf, SigmoidParams Params, double finalScore)
        {
            // 변동성이 너무 낮으면 회귀 기울기도 노이즈일 가능성이 커서 방향을 만들지 않는다.
            PurpleMetrics filterMetric = htf ?? ltf;
            if (filterMetric.BBW < Params.BBWMinThreshold)
                return 0;

            // CalcRegressionScore는 -1~1 범위의 방향성 점수다.
            // MinTrendMagnitude를 동일하게 방향 인정 임계값으로 사용한다.
            double regressionScore = CalcRegressionScore(ltf, htf);
            double threshold = Math.Abs(Params.MinTrendMagnitude);

            if (filterMetric.LRResult.Slope > 0 && ltf.LRResult.Slope > 0 && regressionScore > threshold) return 1;
            if (filterMetric.LRResult.Slope < 0 && ltf.LRResult.Slope < 0 && regressionScore < threshold) return -1;

            return 0;
        }

        /// <summary>
        /// 이 값으로 추세의 방향을 결정할 것임.
        /// </summary>
        /// <param name="ltf"></param>
        /// <param name="htf"></param>
        /// <returns></returns>
        public static double CalcRegressionScore(PurpleMetrics ltf, PurpleMetrics? htf)
        {
            //LTF/HTF의 선형회귀 결과를 활용하여 추세의 강도와 방향을 점수화한다.
            //ltf.LRResult >> LinearRegressionResult 객체
            //htf?.LRResult>> LinearRegressionResult 객체

            // LTF 단독 점수는 반드시 계산한다. HTF가 없으면 이 값을 그대로 추세 방향 점수로 사용한다.
            double ltfScore = ScoreMetric(ltf);
            if (htf == null)
                return ltfScore;

            // HTF가 있으면 큰 시간대 추세를 더 신뢰하여 HTF 60%, LTF 40%로 합성한다.
            double htfScore = ScoreMetric(htf);
            return Math.Clamp((ltfScore * 0.4) + (htfScore * 0.6), -1.0, 1.0);

            static double ScoreMetric(PurpleMetrics m)
            {
                var lr = m.LRResult;

                // 회귀 결과가 계산 불가능한 상태면 중립(0)으로 처리한다.
                if (!IsFinite(lr.Slope) ||
                    !IsFinite(lr.StdDev) ||
                    !IsFinite(lr.RSquared) ||
                    !IsFinite(lr.CurrentBasis) ||
                    lr.CurrentBasis == 0)
                {
                    return 0;
                }

                // R-Squared는 회귀선 신뢰도다. 0이면 추세 점수를 낼 근거가 없으므로 중립 처리한다.
                double reliability = Math.Clamp(lr.RSquared, 0.0, 1.0);
                if (reliability == 0)
                    return 0;

                // Slope를 현재 회귀 중심가 대비 비율로 정규화한 뒤 tanh로 -1~1 범위에 포화시킨다.
                // 양수는 상승 추세, 음수는 하락 추세를 의미한다.
                double slopePct = lr.Slope / Math.Abs(lr.CurrentBasis);
                double slopeScore = Math.Tanh(slopePct * 1000.0);

                // 현재 종가가 회귀 중심선 대비 어느 쪽에 있는지 반영한다.
                // StdDev 2배를 채널 폭으로 보고, 중심선 위면 양수/아래면 음수 점수를 준다.
                double positionScore = 0;
                if (lr.StdDev > double.Epsilon)
                {
                    double distanceFromBasis = ((double)m.Close - lr.CurrentBasis) / (lr.StdDev * 2.0);
                    positionScore = Math.Clamp(distanceFromBasis, -1.0, 1.0);
                }

                // 현재 기울기의 절대값이 직전 봉보다 강해졌는지 확인한다.
                // 추세 방향으로 기울기가 커지면 가속 보너스, 약해지면 감점이 된다.
                double prevAbsSlope = Math.Abs(m.PrevLRResult.Slope);
                double currAbsSlope = Math.Abs(lr.Slope);
                double slopeBase = Math.Max(Math.Max(currAbsSlope, prevAbsSlope), double.Epsilon);
                double acceleration = (currAbsSlope - prevAbsSlope) / slopeBase;
                double accelerationScore = Math.Sign(lr.Slope) * Math.Clamp(acceleration, -1.0, 1.0);

                // 기울기 자체를 가장 크게 보고, 가격 위치와 기울기 가속도를 보조 신호로 섞는다.
                double rawScore =
                    (slopeScore * 0.70) +
                    (positionScore * 0.20) +
                    (accelerationScore * 0.10);

                // 마지막에 R-Squared 신뢰도를 곱해 노이즈 회귀선의 영향력을 낮춘다.
                return Math.Clamp(rawScore * reliability, -1.0, 1.0);
            }

            // NaN/Infinity가 들어오면 이후 Math 연산 전체가 오염되므로 사전에 걸러낸다.
            static bool IsFinite(double value)
                => !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// 현재 보유 방향(heldDir)에 불리한 반전 조짐을 0~1로 점수화한다.
        /// Long 보유 중이면 상승 모멘텀 둔화/과매수 후 자금 이탈/상승 기울기 약화를 찾고,
        /// Short 보유 중이면 그 반대 패턴을 찾는다.
        /// </summary>
        public static double CalcReversalStrength(PurpleMetrics ltf, PurpleMetrics? htf, int heldDir, double minTrendMagnitude)
        {
            if (heldDir == 0) return 0;

            double score = 0;

            // Histogram이 2봉 연속 보유 방향의 반대로 둔화되는지 확인한다.
            bool histDecel = heldDir == 1
                ? ltf.PPrevHistogram > 0
                  && ltf.PPrevHistogram > ltf.PrevHistogram && ltf.PrevHistogram > ltf.Histogram
                : ltf.PPrevHistogram < 0
                  && ltf.PPrevHistogram < ltf.PrevHistogram && ltf.PrevHistogram < ltf.Histogram;
            if (histDecel) score += 0.40;

            // 가격 위치(SPb)가 극단에 있고, 자금/수급(SMb)이 보유 방향과 반대로 움직이면 다이버전스로 본다.
            bool spbOverbought = heldDir == 1 ? ltf.SPb > 0.70 : ltf.SPb < 0.30;
            bool smbDiverging = heldDir == 1 ? ltf.SMb < ltf.PrevSMb
                                              : ltf.SMb > ltf.PrevSMb;
            if (spbOverbought && smbDiverging) score += 0.30;

            // 선형회귀 기울기가 보유 방향으로는 여전히 같은 부호지만 힘이 약해지는지 확인한다.
            bool slopeWeakening = heldDir == 1
                ? ltf.PrevLRResult.Slope > 0 && ltf.LRResult.Slope < ltf.PrevLRResult.Slope
                : ltf.PrevLRResult.Slope < 0 && ltf.LRResult.Slope > ltf.PrevLRResult.Slope;
            if (slopeWeakening) score += 0.15;

            if (htf != null)
            {
                // 큰 추세는 아직 유지되지만 LTF 모멘텀이 먼저 깨지면 조기 위험 신호로 가산한다.
                bool htfStillTrending = heldDir == 1
                    ? htf.Trend > minTrendMagnitude
                    : htf.Trend < -minTrendMagnitude;

                bool ltfMomBroken = heldDir == 1
                    ? ltf.Histogram < 0 || ltf.Histogram < ltf.PrevHistogram * 0.5
                    : ltf.Histogram > 0 || ltf.Histogram > ltf.PrevHistogram * 0.5;

                if (htfStillTrending && ltfMomBroken) score += 0.25;
            }

            return Math.Min(1.0, score);
        }

        // 레거시/보조 점수 함수.
        // OnBarClose의 주문 결정은 PurpleScoringEngine.GetPurpleFinalScore/GetBreakdown을 직접 사용하므로
        // 이 메서드는 현재 SigmoidPositionManager의 메인 주문 로직에는 참여하지 않는다.
        public static double GetPurpleScore(PurpleMetrics mltf, PurpleMetrics mhtf)
        {
            double scoreltf = CalculateOptimizedScore(mltf);
            double scorehtf = CalculateOptimizedScore(mhtf);

            double finalScore = (scoreltf * 0.4) + (scorehtf * 0.6);

            return finalScore;
        }

        /// <summary>
        /// 지표의 위치와 기울기를 분석하여 최적화된 점수를 산출합니다.
        /// PurpleScoringEngine.CalculateOptimizedScore와 거의 같은 역할의 인스턴스 버전으로 보이며,
        /// 현재 OnBarClose에서는 호출되지 않는다.
        /// </summary>
        private static double CalculateOptimizedScore(PurpleMetrics m)
        {
            // --- 1. 추세 섹터 (Max 60점) ---
            double trendScore = 30.0;

            // Trend 절대 위치 (Max 15점 반영)
            double trendPos = Math.Max(-15, Math.Min(15, m.Trend * 10));

            // 가속도 동조화 필터 (Max 15점 반영)
            double momFactor = 0;
            bool isTrendRising = m.Trend > m.PrevTrend;
            bool isMomRising = m.Histogram > m.PrevHistogram;

            if (isTrendRising == isMomRising)
                momFactor = isTrendRising ? 20 : -20; // 추세 가속
            else
                momFactor = isTrendRising ? 5 : -5;   // 추세 둔화(다이버전스 경고)

            trendScore += trendPos + momFactor;

            // --- 2. 에너지 섹터 (Max 40점) ---
            double energyScore = 20.0;

            // 가격 에너지 (s-%b) 및 자금 에너지 (s-%m) 분석
            // 위치(20) + 기울기(40) 가중치 모델
            //double pbVal = (m.sPb - 0.5);
            double pbSlope = (m.SPb - m.PrevSPb);
            //double pbComponent = (pbVal * 20) + (pbSlope * 40);

            //double pmVal = (m.sMb - 0.5);
            double pmSlope = (m.SMb - m.PrevSMb);
            //double pmComponent = (pmVal * 20) + (pmSlope * 40);

            // 에너지 위치 점수
            double energyPos = ((m.SPb - 0.5) * 10) + ((m.SMb - 0.5) * 10);
            // 에너지 방향성 (기울기 가중치 강화)
            double energyDirection = (pbSlope * 30) + (pmSlope * 30);

            // 자금 흐름 역행(Divergence) 패널티
            double divergencePenalty = 0;
            if (pbSlope > 0 && pmSlope < 0) divergencePenalty = -10; // 가격 상승/자금 이탈
            if (pbSlope < 0 && pmSlope > 0) divergencePenalty = 10;  // 가격 하락/자금 유입
            energyScore += energyPos + energyDirection + divergencePenalty;
            //energyScore += (pbComponent * 0.4) + (pmComponent * 0.4) + divergencePenalty;
            // --- 3. 최종 점수 합성 및 필터링 ---
            double rawScore = Math.Clamp(trendScore + energyScore, 0, 100);
            return Math.Max(0, Math.Min(100, rawScore));
        }
    }
}