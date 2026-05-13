namespace PurpleStrategy.Sigmoid
{
    // [2026-05-14] HTF Trend axis = Linear Regression Slope (정규화: Slope/Close = % per bar).
    //   MACD(Trend) axis 는 N=535 RT 백테스트에서 gross-edge ~0 으로 사망 → LR axis 로 swap.
    //   신호 = sign(htf.LRResult.Slope/htf.Close), confidence gate = htf.LRResult.RSquared ≥ MinRSquared.
    //   strength = min(1, |Slope/Close| / TrendFullScale).
    public record SigmoidParams
    {
        // ── SigmoidParams 추정 최소/최대 범위 ─────────────────────────────
        // - htf.LRResult.Slope / htf.Close = "fraction per bar" (BTC 2h HTF 정상 trend ≈ 0.001~0.005)
        // - htf.LRResult.RSquared = 0~1 regression confidence
        // - PurpleMetrics.BBW = 최근 120봉 max 대비 % (0~100 정규화)
        //
        // Steepness                 : 0 ~ 15        (0=항상 0.5, 3=완만, 10 이상은 급격)
        // TrendFullScale            : 0.001 ~ 0.01  (|Slope/Close|=이 값에서 strength 포화)
        // MinTrendMagnitude         : 0 ~ 0.005     (방향 인정 최소 |Slope/Close|)
        // MinRSquared               : 0 ~ 1         (regression confidence gate, 0.2~0.5 권장)
        // BBWMinThreshold           : 0 ~ 100       (실전 필터 10~30 근방)
        // MaxWeightChangePerBar     : 0.001 ~ 0.01  (봉당 증량 속도 제한)
        // ExitSpeedMultiplier       : 1 ~ 100       (1=증량과 동일, 100=즉시 청산)
        // MddDefenseThreshold       : 0 ~ 1         (현재 DD 비율)
        // MddDefenseMaxWeight       : 0 ~ MaxWeight (방어 모드 포지션 상한)
        // MddDefenseExitThreshold   : 0 ~ MddDefenseThreshold (히스테리시스 해제선)
        // MaxWeight                 : 0 ~ 1         (계좌 대비 목표 포지션 비중)

        // strength → Sigmoid(strength × Steepness) 변환 가파름.
        // P2 (floor 제거) 적용 후 0.5 근방 분해능: 3=완만 (strength 0.3→0.422, 0.5→0.635), 10=급격 (0.3→0.905).
        // 0~15 범위에서 3 근방이 적당 (binary 화 방지).
        public double Steepness { get; init; } = 3;

        // |Slope/Close| 를 strength=1.0 으로 정규화하는 기준값 (= % per bar at saturation).
        // strength = min(1.0, |Slope/Close| / TrendFullScale).
        // 0.003 → 0.3% per bar 에서 포화. BTC 2h HTF 강한 trend 가 0.3~0.5%/bar 수준.
        public double TrendFullScale { get; init; } = 0.003;

        // |Slope/Close| 가 이 값보다 작으면 방향 없음으로 본다 (노이즈 필터).
        // 0.0005 = 0.05% per bar 미만의 기울기는 무시.
        public double MinTrendMagnitude { get; init; } = 0.0005;

        // LR confidence gate. htf.LRResult.RSquared < 이 값이면 dir=0.
        // 0.3 = 회귀선 fit 이 분산의 30% 미만을 설명하면 신뢰 안 함.
        public double MinRSquared { get; init; } = 0.3;

        // 볼린저밴드폭(BBW) 정규화 값이 이 값보다 작으면 횡보로 보고 dir=0.
        // PurpleMetrics.BBW 는 0~100 정규화 (120봉 max 대비 %).
        // 0~100 범위에서 15~30 근방이 적당.
        public double BBWMinThreshold { get; init; } = 20;

        // 한 봉당 늘릴 수 있는 최대 비중 변화량.
        // 0.001~0.01 범위에서 0.003 근방이 적당 (한 봉당 0.3% 변화 허용).
        public double MaxWeightChangePerBar { get; init; } = 0.003;

        // 감량 방향 (delta < 0) 일 때 MaxWeightChangePerBar 에 곱하는 배수.
        // 1~100 범위에서 100 근방 (사실상 즉시 청산 허용) 권장.
        public double ExitSpeedMultiplier { get; init; } = 100;

        // 최고 잔고 대비 현재 DD 가 이 값 이상이면 MDD 방어 모드 진입.
        // 0~1 범위에서 0.15 근방.
        public double MddDefenseThreshold { get; init; } = 0.15;

        // MDD 방어 모드에서 사용하는 최대 비중 상한 (MaxWeight 보다 작게).
        public double MddDefenseMaxWeight { get; init; } = 0.10;

        // DD 가 이 값 아래로 회복되면 MDD 방어 모드 종료 (히스테리시스).
        public double MddDefenseExitThreshold { get; init; } = 0.05;

        // 정상 모드에서 허용하는 최대 포지션 비중. TargetNotional = balance * leverage * weight.
        public double MaxWeight { get; init; } = 0.3;
    }
}
