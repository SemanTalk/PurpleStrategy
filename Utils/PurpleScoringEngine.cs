using System;
using System.Diagnostics;
using PurpleStrategy.Models;

namespace PurpleStrategy.Utils
{
    /*
     * ─────────────────────────────────────────────────────────────────────────
     *  PurpleScoringEngine  (Refactored & Hardened)
     * ─────────────────────────────────────────────────────────────────────────
     *
     *  목적 (Purpose)
     *  --------------------------------------------------------------
     *  PurpleMetrics 의 다중 시계열 지표(현재 봉 + 직전 2봉)를 입력받아
     *  현재 봉의 "컨빅션 강도(Conviction Strength)"를 0~100 으로 산출한다.
     *
     *  중요한 해석 규칙
     *  --------------------------------------------------------------
     *   • Total 은 "방향(롱/숏)"이 아니라 "강도(얼마나 강한 신호인가)"다.
     *   • 방향성은 m.Trend 의 부호로 별도 판단해야 한다.
     *   • 따라서 Total = 80 은 "롱 80"이 아니라 "현재 추세 방향에 대한
     *     컨빅션이 80" 이라는 의미다.
     *
     *  5대 하위 점수
     *  --------------------------------------------------------------
     *   [1] Trend        : 추세 절대값 + 전봉/전전봉 대비 가속
     *   [2] Momentum     : Histogram 강도 + 추세 일치 + 가/감속
     *   [3] Alignment    : SPb, SMb, Trend 방향 정렬도
     *   [4] Confirmation : 눌림목(Long) / 저항반등(Short) 위치 적합성
     *   [5] Energy       : 중심선 이탈 + 기울기 + 다이버전스
     *
     *  요구 환경
     *  --------------------------------------------------------------
     *   • C# 9.0 이상 (record 타입 사용)
     *   • PurpleStrategy.Models.PurpleMetrics 에 다음 멤버가 있어야 함:
     *       Trend, PrevTrend, PPrevTrend
     *       Histogram, PrevHistogram, PPrevHistogram
     *       SPb, PrevSPb, PPrevSPb
     *       SMb, PrevSMb, PPrevSMb
     * ─────────────────────────────────────────────────────────────────────────
     */

    public static class PurpleScoringEngine
    {
        // ═════════════════════════════════════════════════════════════
        //  (A) Weights — 모든 가중치는 동일하게 private const 로 통일
        //      (원본은 W_Energy 만 public 이었음 → 의도되지 않은 비대칭 수정)
        // ═════════════════════════════════════════════════════════════
        public const double W_Trend = 20.0;

        private const double W_Momentum = 20.0;
        private const double W_Alignment = 15.0;
        private const double W_Confirmation = 25.0;
        private const double W_Energy = 20.0;

        internal static double GetWEnergy() => W_Energy; // 테스트용: 리플렉션 대신 public 메서드로 접근

        /// <summary>
        /// 가중치 합. 변경 시 자동으로 재계산되며 Total 정규화에 사용된다.
        /// (디버그 빌드에서 100 인지 검증한다.)
        /// </summary>
        private const double TotalWeight =
            W_Trend + W_Momentum + W_Alignment + W_Confirmation + W_Energy;

        // ═════════════════════════════════════════════════════════════
        //  (B) Sensitivity / Threshold — tanh 포화함수의 입력 스케일러
        // ═════════════════════════════════════════════════════════════

        /// <summary>Trend 값을 tanh 에 넣기 전 곱해주는 감도. 클수록 작은 변화에 민감.</summary>
        private const double TrendSensitivity = 10.0;

        /// <summary>Histogram 감도.</summary>
        private const double HistSensitivity = 30.0;

        /// <summary>Confirmation 의 가우시안형 위치 적합도 폭 (작을수록 넓게 인정).</summary>
        private const double ConfirmWidth = 8.0;

        /// <summary>Histogram 과 Trend 의 부호가 충돌할 때 Momentum 에 곱하는 패널티.</summary>
        private const double ConflictPenalty = 0.40;

        /// <summary>가속(Acceleration) 시 부여하는 보너스 비율.</summary>
        private const double AccelBonus = 0.10;

        /// <summary>SPb/SMb 다이버전스 발생 시 slope 점수에 곱하는 패널티.</summary>
        private const double DivergencePenalty = 0.30;

        /// <summary>Trend 가 0 근방인지 판정하는 임계값 (Neutral 영역).</summary>
        private const double TrendThreshold = 0.02;

        /// <summary>Trend 가 Neutral 일 때 Confirmation/Alignment 에 부여하는 기본 비율.</summary>
        private const double NeutralTrendScore = 0.33;

        // ───── 가독성을 위해 상수화한 매직 넘버들 ─────

        /// <summary>Long 셋업에서 SPb 의 이상적 눌림목 위치.</summary>
        private const double LongPullbackZone = 0.25;

        /// <summary>Short 셋업에서 SPb 의 이상적 저항반등 위치.</summary>
        private const double ShortRejectZone = 0.75;

        /// <summary>SPb/SMb 가 0.5 (중립) 에서 얼마나 떨어져 있는지를 0~1 로 매핑할 때의 게인.</summary>
        private const double BiasNormalizerGain = 4.0;

        /// <summary>Energy 의 위치 점수에서 사용하는 tanh 감도.</summary>
        private const double EnergyPosSensitivity = 6.0;

        /// <summary>Energy 의 기울기(slope) 점수에서 사용하는 tanh 감도.</summary>
        private const double EnergySlopeSensitivity = 60.0;

        /// <summary>Confirmation 에서 추세 지속(Trend·SPb·PPrevSPb 단조 증가) 보너스.</summary>
        private const double TrendContinuationBonus = 1.05;

        /// <summary>Alignment 에서 정렬이 부족하고 추세가 약화될 때 부여하는 감점.</summary>
        private const double WeakAlignmentMultiplier = 0.90;

        /// <summary>Momentum 감속 시 차감 비율 (음수 보너스).</summary>
        private const double DecelPenalty = -0.10;

        /// <summary>Confirmation 에서 SMb 가중 (위치 0.6 + SMb 0.4).</summary>
        private const double ConfirmSpbWeight = 0.6;

        private const double ConfirmSmbWeight = 0.4;

        // ═════════════════════════════════════════════════════════════
        //  (C) Result type
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// 컨빅션 분해 결과.
        /// Total 은 0~100 정규화 값이며, 나머지는 각 가중치 상한까지의 raw 점수다.
        /// </summary>
        public record ConvictionBreakdown(
            double Total,
            double Trend,
            double Momentum,
            double Alignment,
            double Confirmation,
            double Energy)
        {
            /// <summary>유효하지 않은 입력에 대한 사전 정의 결과.</summary>
            public static ConvictionBreakdown Invalid { get; } =
                new(0, 0, 0, 0, 0, 0);

            /// <summary>모든 점수가 0 인 경우 (= Invalid 와 구분되지 않음에 유의).</summary>
            public bool IsZero =>
                Total == 0 && Trend == 0 && Momentum == 0
                && Alignment == 0 && Confirmation == 0 && Energy == 0;
        }

        // ═════════════════════════════════════════════════════════════
        //  (D) Static ctor — 가중치 합 자체검증 (디버그 빌드 한정)
        // ═════════════════════════════════════════════════════════════
        static PurpleScoringEngine()
        {
            // 가중치 변경 시 100 스케일이 깨졌는지 즉시 알 수 있도록 한다.
            Debug.Assert(
                Math.Abs(TotalWeight - 100.0) < 1e-9,
                $"TotalWeight 가 100 이 아님: {TotalWeight}");
        }

        // ═════════════════════════════════════════════════════════════
        //  (E) Public API
        // ═════════════════════════════════════════════════════════════
        private static double _lastSmoothScore = 50.0; // 이전 바의 평활화된 점수 저장

        private const double SmoothingFactor = 0.25; // 점수 변화의 부드러움 정도 (0.1 ~ 0.4 추천)

        public static double GetPurpleFinalScore(PurpleMetrics mltf, PurpleMetrics? mhtf)
        {
            double finalScore = 0;
            double scoreltf = CalculateOptimizedScore(mltf);
            double scorehtf = mhtf != null ? CalculateOptimizedScore(mhtf) : 0;
            if (mhtf != null)
            {
                finalScore = (scoreltf * 0.4) + (scorehtf * 0.6);
            }
            else
            {
                finalScore = scoreltf;
            }

            return finalScore;
        }

        /// <summary>
        /// 지표의 위치와 기울기를 분석하여 최적화된 점수를 산출합니다.
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
            // [보안전략] 노이즈 제거를 위한 EMA 평활화
            double smoothScore = (rawScore * SmoothingFactor) + (_lastSmoothScore * (1 - SmoothingFactor));
            _lastSmoothScore = smoothScore;
            return Math.Max(0, Math.Min(100, smoothScore));
        }

        /// <summary>
        /// PurpleMetrics 로부터 컨빅션 분해 점수를 산출한다.
        /// 입력이 유효하지 않으면 <see cref="ConvictionBreakdown.Invalid"/> 를 반환한다.
        /// </summary>
        public static ConvictionBreakdown GetBreakdown(PurpleMetrics m)
        {
            if (!IsValid(m))
                return ConvictionBreakdown.Invalid;

            double trend = CalculateTrendScore(m);
            double momentum = CalculateMomentumScore(m);
            double alignment = CalculateAlignmentScore(m);
            double confirmation = CalculateConfirmationScore(m);
            double energy = CalculateEnergyScore(m);

            double rawSum = trend + momentum + alignment + confirmation + energy;
            double total = Clamp(rawSum / TotalWeight * 100.0, 0.0, 100.0);

            return new ConvictionBreakdown(total, trend, momentum, alignment, confirmation, energy);
        }

        /// <summary>
        /// 0(0/100) 과 "유효한 점수 0" 을 호출자가 명확히 구분해야 할 때 사용.
        /// </summary>
        public static bool TryGetBreakdown(PurpleMetrics m, out ConvictionBreakdown result)
        {
            if (!IsValid(m))
            {
                result = ConvictionBreakdown.Invalid;
                return false;
            }

            result = GetBreakdown(m);
            return true;
        }

        // ═════════════════════════════════════════════════════════════
        //  [1] Trend
        //  현재 Trend 의 절대 강도(tanh saturating) +
        //  전봉/전전봉 대비 가속도 보너스를 합산한다.
        //
        //  - accelerating    : |Trend| 가 2봉 연속 증가
        //  - reAccelerating  : 한 번 줄었다가 다시 증가하는 패턴
        //  주의: baseScore 가 W_Trend 에 가까우면 보너스가 Clamp 에 의해 잘릴 수 있다.
        //        이는 "포화 후 추가 가속은 무의미"하다는 의도된 동작이다.
        // ═════════════════════════════════════════════════════════════
        private static double CalculateTrendScore(PurpleMetrics m)
        {
            double baseScore = W_Trend * Math.Tanh(Math.Abs(m.Trend) * TrendSensitivity);

            double delta1 = Math.Abs(m.Trend) - Math.Abs(m.PrevTrend);
            double delta2 = Math.Abs(m.PrevTrend) - Math.Abs(m.PPrevTrend);

            bool accelerating = delta1 > 0.0 && delta2 >= 0.0;
            bool reAccelerating = delta1 > 0.0 && delta2 < 0.0;

            double bonus = 0.0;
            if (accelerating) bonus = baseScore * AccelBonus;
            else if (reAccelerating) bonus = baseScore * (AccelBonus * 0.5);

            return Clamp(baseScore + bonus, 0.0, W_Trend);
        }

        // ═════════════════════════════════════════════════════════════
        //  [2] Momentum
        //  Histogram 절대 강도를 base 로 하여,
        //   ① Histogram 부호와 Trend 부호가 충돌하면 ConflictPenalty
        //   ② 2봉 연속 가속이면 +AccelBonus
        //   ③ 2봉 연속 감속이면 -DecelPenalty
        // ═════════════════════════════════════════════════════════════
        private static double CalculateMomentumScore(PurpleMetrics m)
        {
            double baseScore = W_Momentum * Math.Tanh(Math.Abs(m.Histogram) * HistSensitivity);

            bool trendBull = m.Trend > 0.0;
            bool histBull = m.Histogram > 0.0;

            // ① 충돌 패널티 (Histogram 이 정확히 0 이면 baseScore 도 0 → 의미 없음)
            if (m.Histogram != 0.0 && histBull != trendBull)
                baseScore *= ConflictPenalty;

            double currAbs = Math.Abs(m.Histogram);
            double prevAbs = Math.Abs(m.PrevHistogram);
            double pprevAbs = Math.Abs(m.PPrevHistogram);

            bool accelerating = currAbs > prevAbs && prevAbs >= pprevAbs;
            bool decelerating = currAbs < prevAbs && prevAbs < pprevAbs;

            double bonus = 0.0;
            if (accelerating) bonus = baseScore * AccelBonus;   // +10%
            else if (decelerating) bonus = baseScore * DecelPenalty; // -10%

            return Clamp(baseScore + bonus, 0.0, W_Momentum);
        }

        // ═════════════════════════════════════════════════════════════
        //  [3] Alignment
        //  세 신호(Trend, SPb>0.5, SMb>0.5)의 방향 정렬도(0~3)를 평가한다.
        //
        //  개선점: Trend 부호 판정에 TrendThreshold 를 적용하여
        //         Trend≈0 일 때 강제로 "Bear" 로 분류되던 원본 버그를 수정.
        //         Neutral 시에는 NeutralTrendScore 비율로 안전하게 반환한다.
        // ═════════════════════════════════════════════════════════════
        private static double CalculateAlignmentScore(PurpleMetrics m)
        {
            // Trend 가 임계값 이하라면 alignment 는 산정하지 않고 중립값 반환
            if (Math.Abs(m.Trend) < TrendThreshold)
                return W_Alignment * NeutralTrendScore;

            bool trendBull = m.Trend > 0.0;
            bool spbBull = m.SPb > 0.5;
            bool smbBull = m.SMb > 0.5;

            int aligned = 0;
            if (spbBull == trendBull) aligned++;
            if (smbBull == trendBull) aligned++;
            if (spbBull == smbBull) aligned++;

            // 중심선(0.5) 으로부터의 이탈을 0~1 로 정규화한 "신호 강도"
            double spbStrength = Clamp01(Math.Abs(m.SPb - 0.5) * BiasNormalizerGain);
            double smbStrength = Clamp01(Math.Abs(m.SMb - 0.5) * BiasNormalizerGain);

            // 기하평균: 한 쪽이 약하면 전체 신뢰도가 낮아지는 보수적 결합
            double signalPower = Math.Sqrt(spbStrength * smbStrength);

            double alignRatio = aligned / 3.0;
            double score = W_Alignment * alignRatio * (0.25 + 0.75 * signalPower);

            // SPb / SMb 가 자기 bias 와 같은 방향으로 "더" 움직이고 있는지
            bool spbMovingWithBias =
                (spbBull && m.SPb > m.PrevSPb) ||
                (!spbBull && m.SPb < m.PrevSPb);

            bool smbMovingWithBias =
                (smbBull && m.SMb > m.PrevSMb) ||
                (!smbBull && m.SMb < m.PrevSMb);

            // Trend 가 2봉 연속 강해지고 있는지
            bool trendSustained =
                Math.Abs(m.Trend) >= Math.Abs(m.PrevTrend) &&
                Math.Abs(m.PrevTrend) >= Math.Abs(m.PPrevTrend);

            // SPb/SMb 가 모두 같은 bias 로 움직이고 그 bias 가 Trend 와 일치 → 가속 보너스
            if (spbMovingWithBias && smbMovingWithBias && spbBull == trendBull)
                score *= 1.0 + AccelBonus;

            // 정렬도 부족 + 추세 약화 → 약한 감점
            if (!trendSustained && aligned < 3)
                score *= WeakAlignmentMultiplier;

            return Clamp(score, 0.0, W_Alignment);
        }

        // ═════════════════════════════════════════════════════════════
        //  [4] Confirmation
        //  - Long  : SPb 가 LongPullbackZone(0.25) 부근에서 반등할수록 ↑,
        //            SMb 는 낮을수록 ↑ (과매수 회피)
        //  - Short : SPb 가 ShortRejectZone(0.75) 부근에서 거절될수록 ↑,
        //            SMb 는 높을수록 ↑ (과매도 회피)
        //  - Neutral(|Trend| < 임계) : 중립 점수만 부여
        //
        //  개선점: SMb 매핑식을 PullbackQualityFromSmb / RejectQualityFromSmb
        //          헬퍼로 분리하여 의미를 명확히 함.
        // ═════════════════════════════════════════════════════════════
        private static double CalculateConfirmationScore(PurpleMetrics m)
        {
            // ─ Long 셋업 ─
            if (m.Trend > TrendThreshold)
            {
                double dist = m.SPb - LongPullbackZone;
                double spbFactor = Math.Exp(-dist * dist * ConfirmWidth);
                double smbFactor = PullbackQualityFromSmb(m.SMb);

                double score = W_Confirmation
                             * spbFactor
                             * (ConfirmSpbWeight + ConfirmSmbWeight * smbFactor);

                // SPb 가 3봉 연속 상승 → 추세 지속 보너스
                if (m.SPb > m.PrevSPb && m.PrevSPb > m.PPrevSPb)
                    score *= TrendContinuationBonus;

                return Clamp(score, 0.0, W_Confirmation);
            }

            // ─ Short 셋업 ─
            if (m.Trend < -TrendThreshold)
            {
                double dist = m.SPb - ShortRejectZone;
                double spbFactor = Math.Exp(-dist * dist * ConfirmWidth);
                double smbFactor = RejectQualityFromSmb(m.SMb);

                double score = W_Confirmation
                             * spbFactor
                             * (ConfirmSpbWeight + ConfirmSmbWeight * smbFactor);

                if (m.SPb < m.PrevSPb && m.PrevSPb < m.PPrevSPb)
                    score *= TrendContinuationBonus;

                return Clamp(score, 0.0, W_Confirmation);
            }

            // ─ Neutral ─
            return W_Confirmation * NeutralTrendScore;
        }

        /// <summary>
        /// Long 셋업에서 SMb 품질 (낮을수록 좋음).
        /// SMb 0.0 → 1.0,  0.5 → 0.5,  ≥0.75 → 0
        /// </summary>
        private static double PullbackQualityFromSmb(double smb)
            => Clamp01(1.5 - 2.0 * smb);

        /// <summary>
        /// Short 셋업에서 SMb 품질 (높을수록 좋음).
        /// SMb 1.0 → 1.0,  0.5 → 0.5,  ≤0.25 → 0
        /// </summary>
        private static double RejectQualityFromSmb(double smb)
            => Clamp01(2.0 * smb - 0.5);

        // ═════════════════════════════════════════════════════════════
        //  [5] Energy
        //  ① 위치 점수 : SPb/SMb 가 중심선 0.5 에서 얼마나 떨어졌는지
        //  ② 기울기 점수: SPb/SMb 의 1봉 변화량
        //  ③ 다이버전스: SPb 와 SMb slope 부호가 다르면 페널티
        //  ④ Trend 가 3봉 연속 강해질 때 소폭 보너스
        // ═════════════════════════════════════════════════════════════
        private static double CalculateEnergyScore(PurpleMetrics m)
        {
            double pbSlope = m.SPb - m.PrevSPb;
            double pmSlope = m.SMb - m.PrevSMb;

            // ① 위치 점수 (각각 W_Energy/4 = 5 상한)
            double posScore =
                (W_Energy / 4.0) * Math.Tanh(Math.Abs(m.SPb - 0.5) * EnergyPosSensitivity) +
                (W_Energy / 4.0) * Math.Tanh(Math.Abs(m.SMb - 0.5) * EnergyPosSensitivity);

            // ② 기울기 점수 (각각 W_Energy/4 = 5 상한)
            double spbMomentum = (W_Energy / 4.0) * Math.Tanh(Math.Abs(pbSlope) * EnergySlopeSensitivity);
            double smbMomentum = (W_Energy / 4.0) * Math.Tanh(Math.Abs(pmSlope) * EnergySlopeSensitivity);

            // ③ 다이버전스 패널티
            bool diverging =
                pbSlope != 0.0 && pmSlope != 0.0 &&
                ((pbSlope > 0.0) != (pmSlope > 0.0));

            double slopeScore = (spbMomentum + smbMomentum)
                              * (diverging ? DivergencePenalty : 1.0);

            double score = posScore + slopeScore;

            // ④ Trend 가 2봉 연속 강해지는 중 → 소폭 보너스
            if (Math.Abs(m.Trend) > Math.Abs(m.PrevTrend) &&
                Math.Abs(m.PrevTrend) > Math.Abs(m.PPrevTrend))
            {
                score *= TrendContinuationBonus;
            }

            return Clamp(score, 0.0, W_Energy);
        }

        // ═════════════════════════════════════════════════════════════
        //  Validation / Helpers
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// 모든 입력 필드가 NaN/Infinity 가 아닌지 검사한다.
        /// null 입력은 false 로 처리하므로 호출자는 GetBreakdown 의 0 결과를
        /// "유효한 0" 으로 오인하지 않도록 TryGetBreakdown 사용을 권장한다.
        /// </summary>
        private static bool IsValid(PurpleMetrics m)
        {
            if (m is null) return false;

            return IsFinite(m.Trend) &&
                   IsFinite(m.Histogram) &&
                   IsFinite(m.PrevTrend) &&
                   IsFinite(m.PPrevTrend) &&
                   IsFinite(m.SPb) &&
                   IsFinite(m.SMb) &&
                   IsFinite(m.PrevSPb) &&
                   IsFinite(m.PPrevSPb) &&
                   IsFinite(m.PrevSMb) &&
                   IsFinite(m.PPrevSMb) &&
                   IsFinite(m.PrevHistogram) &&
                   IsFinite(m.PPrevHistogram);
        }

        private static bool IsFinite(double x)
            => !double.IsNaN(x) && !double.IsInfinity(x);

        private static double Clamp(double x, double min, double max)
        {
            if (x < min) return min;
            if (x > max) return max;
            return x;
        }

        private static double Clamp01(double x) => Clamp(x, 0.0, 1.0);
    }
}