using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PurpleStrategy.Sigmoid
{
    public record SigmoidParams
    {
        // ── SigmoidParams 추정 최소/최대 범위 ─────────────────────────────
        // 기준:
        // - PurpleScoringEngine ConvictionBreakdown raw 점수 상한:
        //   Trend 0~20, Momentum 0~20, Alignment 0~15, Confirmation 0~25, Energy 0~20
        // - CalcReversalStrength() 반환값은 0~1, BBW는 최근 120봉 밴드폭 기준 0~100 정규화 값
        // - 0으로 나누는 FullScale 계열은 최소값을 0 초과로 둔다.
        //
        // ConfirmFullScale          : 0 초과 ~ 25   (Confirmation raw 상한 25)
        // HeatFullScale             : 0 초과 ~ 35   (Trend 20 + Alignment 15)
        // HeatPenalty               : 0 ~ 1.5       (0=비활성, 1 이상은 강한 과열 감쇄)
        // Steepness                 : 0 ~ 15        (0=항상 0.5, 10 이상은 급격한 반응)
        // MomentumBoost             : 0 ~ 0.5       (Momentum 정규화 보너스 상한)
        // EnergyBoost               : 0 ~ 1.5       (Energy 정규화 보너스 상한)
        // MinTrendMagnitude         : 0 ~ 0.20      (0.02=중립 근방, 0.20=CalcDir 방향 기준)
        // BBWMinThreshold           : 0 ~ 100       (정규화 BBW 전체 범위, 실전 필터는 10~30 근방)
        // MaxWeightChangePerBar     : 0.001 ~ 0.01  (봉당 증량 속도 제한)
        // ExitSpeedMultiplier       : 1 ~ 100       (1=증량과 동일, 100=사실상 즉시 청산)
        // MddDefenseThreshold       : 0 ~ 1         (현재 DD 비율)
        // MddDefenseMaxWeight       : 0 ~ MaxWeight (방어 모드 포지션 상한)
        // MddDefenseExitThreshold   : 0 ~ MddDefenseThreshold (히스테리시스 해제선)
        // ReversalBlockThreshold    : 0 ~ 1         (반전 강도 기반 증량 차단)
        // ReversalSignalThreshold   : 0 ~ 1         (반전 강도 기반 netSignal 감쇠)
        // HtfReversalThreshold      : 0 ~ 1         (0=비활성, 0 초과부터 HTF 반전 차단)
        // MaxWeight                 : 0 ~ 1         (계좌 대비 목표 포지션 비중)

        // Confirmation 원점수(bd.Confirmation)를 0~1로 정규화할 때의 기준값.
        // 값이 작을수록 약한 확인 신호도 빠르게 1에 가까워지며, 0이면 나눗셈 결과가 NaN/Infinity가 될 수 있다.
        // 확인(Confirmation) 점수의 최대 스케일.확인 지표가 이 값에 도달했을 때 신호 강도가 최고점이라는 의미입니다. (정규화 기준)
        // 1~25 (bd.Confirmation raw 상한 25, 실전 10 근방에서 포화 시작)
        public double ConfirmFullScale { get; init; } = 15;

        // Trend + Alignment를 과열(Heat)로 정규화할 때의 기준값.
        // HeatPenalty가 0보다 클 때만 실질적으로 netSignal을 깎으며, 0이면 나눗셈 위험이 있다.
        // 열(Heat, 추세/정렬) 점수의 최대 스케일.열 지표가 이 값에 도달했을 때 가장 강력한 신호라는 의미입니다.
        // 1~35 (Trend 20 + Alignment 15) 범위 내에서 실전에서는 10 근방에서 포화 시작
        public double HeatFullScale { get; init; } = 20;

        // 정규화된 Heat를 신호에서 얼마나 차감할지 결정한다.
        // 높을수록 강하지만 과열된 추세에서 목표 비중이 더 작아진다.
        // 열점수 페널티 계수. Heat Score가 높더라도, 다른 신호(예: Confirmation)와 상충될 경우 이를 감쇄시키는 정도를 조절합니다. (값이 클수록 보수적)
        // 0~1.5 (0=비활성, 0.1~0.3은 온화한 감쇄, 0.5 이상은 강한 감쇄) 범위에서 실전에서는 0.1 근방이 적당해 보임
        public double HeatPenalty { get; init; } = 0.1;

        // netSignal을 Sigmoid에 넣기 전 곱하는 민감도.
        // 높을수록 작은 신호 차이에도 목표 비중이 급격히 변한다.
        // 시그모이드 함수의 가파른 정도. NetSignal을 Weight로 변환할 때 얼마나 급격하게 반응할지를 결정합니다. (가파를수록 신호 변화에 민감)
        // 0~15 범위에서 실전에서는 10 근방이 적당해 보임 (0이면 항상 0.5 → 방향은 있지만 비중 변화 없음, 10 이상은 0.5 근처에서 급격히 0 또는 MaxWeight로 이동)
        public double Steepness { get; init; } = 10;

        // Histogram이 진입 방향과 같은 부호일 때 추가하는 모멘텀 보너스의 최대치.
        // 0이면 모멘텀 보너스를 사용하지 않는다.
        // 모멘텀 강도 부스팅 값. 추세 모멘텀이 감지되었을 때, 신호 점수에 추가적으로 가산할 최대 크기입니다. (양수일수록 긍정적 영향을 크게 받음)
        // 0~0.5 범위에서 실전에서는 0.1 근방이 적당해 보임 (0이면 모멘텀 보너스 비활성, 0.1은 모멘텀 점수의 10%가 netSignal에 추가되는 효과)
        public double MomentumBoost { get; init; } = 0.1;

        // Energy 점수를 netSignal에 얼마나 더할지 결정한다.
        // 0이면 Energy는 로그/디버깅에는 남지만 비중 산식에는 반영되지 않는다.
        // 에너지 지표 부스팅 값. 에너지(Energy) 지표가 감지되었을 때 신호 점수에 추가적으로 가산할 최대 크기입니다.
        // 0~1,5 범위에서 실전에서는 0.2 근방이 적당해 보임 (0이면 에너지 점수 비활성, 0.2는 에너지 점수의 20%가 netSignal에 추가되는 효과)
        public double EnergyBoost { get; init; } = 0.2;

        // Trend 절대값이 이 값보다 작으면 방향성이 없다고 본다.
        // 0이면 아주 작은 Trend도 EMA 조건만 맞으면 방향으로 인정될 수 있다.
        // 최소 추세 강도 임계값. 방향(dir)을 결정하기 위해 필요한 최소한의 추세를 요구합니다.
        // 이보다 약하면 추세가 없는 것으로 간주하여 거래를 중단시킬 수 있습니다.
        // 0~0.2 범위에서 실전에서는 0.01 근방이 적당해 보임 (0.01은 Trend가 1%일 때 방향 인정, 0.02는 2%일 때 인정)
        public double MinTrendMagnitude { get; init; } = 0.01;

        // 볼린저밴드폭(BBW)이 이 값보다 작으면 횡보/저변동으로 보고 방향을 막으려는 임계값.
        // 최소 볼린저밴드 폭(Bollinger Band Width) 임계값.
        // 시장의 변동성(Volatility)이 너무 작으면 진입을 자제하는 최소한의 기준입니다. (변동성이 낮으면 추세 신뢰도 하락)
        // 0~100 범위에서 실전에서는 10~30 근방이 적당해 보임 (값이 클수록 변동성 필터 완화, 10은 매우 엄격한 필터, 30은 완화된 필터)
        public double BBWMinThreshold { get; init; } = 20;

        // 한 봉에서 늘릴 수 있는 최대 비중 변화량.
        // 진입/증량 속도를 제한해 갑작스러운 포지션 확대를 막는다.
        // 최대 웨이트 변화량(상한). 한 봉에 최대 몇 %까지 포지션 사이즈가 바뀔 수 있는지의 기초 제한값입니다. (리스크 관리 핵심)
        // 0.001~0.01 범위에서 실전에서는 0.003 근방이 적당해 보임 (0.003은 한 봉에 최대 0.3% 포지션 변화 허용, 0.01은 최대 1% 허용) >> 고정
        public double MaxWeightChangePerBar { get; init; } = 0.003;

        // 감량/청산 방향(delta < 0)일 때 MaxWeightChangePerBar에 곱하는 배수.
        // 높을수록 위험 축소는 빠르고 진입은 느린 구조가 된다.
        // 청산 속도 배율. 웨이트를 줄일 때(감소 시), MaxWeightChangePerBar를 이 값으로 곱하여 더 공격적으로 청산할 수 있도록 허용합니다.
        // (만약 매수/매도 차이로 설정된 것이라면, 이는 '감소' 제한을 우회하는 방식으로 작동함?)
        // ⚠ DESIGN: 기본값 100에서 maxChange = 0.005 × 100 = 0.5 > MaxWeight(0.3). 한 봉에 포지션 전량 청산 가능 → 사실상 청산 속도 제한이 없다.
        // 1~100 범위에서 실전에서는 100 근방이 적당해 보임 (1은 증량과 동일한 속도로 감량 허용, 100은 사실상 즉시 청산 허용) >> 고정
        public double ExitSpeedMultiplier { get; init; } = 100; //고정 >> 즉시 청산

        // 최고 잔고 대비 현재 잔고의 낙폭이 이 값 이상이면 MDD 방어 모드에 진입한다.
        // MDD 방어 모드 진입 임계값. 최고 자산 대비 하락률(Drawdown)이 이 값에 도달하면, 시스템이 '위험 관리/보호 모드'로 전환됩니다.
        // 0~1 범위에서 실전에서는 0.1~0.2 근방이 적당해 보임 (0.15는 최고점 대비 15% 하락 시 방어 모드 진입) >> 고정
        public double MddDefenseThreshold { get; init; } = 0.15;

        // MDD 방어 모드에서 사용하는 최대 비중 상한.
        // MaxWeight보다 작게 두어 손실 구간의 익스포저를 줄이는 목적이다.
        // MDD 방어 모드 시 최대 웨이트. 위험 모드 진입 시 포지션 크기를 강제로 낮춥니다 (예: 15% 하락 -> Max Weight를 10%로 제한).
        // 0~MaxWeight 범위에서 실전에서는 0.1 근방이 적당해 보임 (0.1은 방어 모드에서 최대 10% 포지션 허용) >> 고정
        public double MddDefenseMaxWeight { get; init; } = 0.10;

        // 현재 DD가 이 값 아래로 회복되면 MDD 방어 모드를 종료한다.
        // 진입 임계값보다 낮게 두어 방어 모드가 자주 켜졌다 꺼지는 현상을 줄인다.
        // MDD 방어 모드 해제 임계값. Drawdown이 이 값 이하로 회복되면, 시스템은 다시 정상적인 트레이딩 모드로 돌아갑니다.
        // 0~MddDefenseThreshold 범위에서 실전에서는 0.05 근방이 적당해 보임 (0.05는 최고점 대비 5% 회복 시 방어 모드 종료) >> 고정
        public double MddDefenseExitThreshold { get; init; } = 0.05;

        // 반전 강도가 이 값 이상이면 목표 비중이 현재 비중보다 커지지 못하게 막는다.
        // 반전 차단 임계값. 반전 신호 강도가 이 이상이면 (예: 과매수/과매도 상태가 심각하여), 포지션 크기 증량을 차단하거나 현 웨이트를 유지하게 만듭니다.
        // 0~1 범위에서 실전에서는 0.4 근방이 적당해 보임 (0.4는 반전 신호가 40% 이상 강할 때 신규 증량 차단) >> 고정
        public double ReversalBlockThreshold { get; init; } = 0.4;

        // 반전 강도가 이 값 이상이면 netSignal 자체를 약화시켜 감량을 유도한다.
        // 반전 신호 약화 임계값. 반전 신호 강도가 이 이상이면, 계산된 최종 점수(netSignal)에 페널티(감쇄)를 적용하여 진입을 주저하게 만듭니다.
        public double ReversalSignalThreshold { get; init; } = 0.6;

        // HTF 지표에서 반전 강도가 이 값 이상이면 이번 봉의 신규 방향을 무효(dir=0)로 만든다.
        // HTF 반전 임계값. HTF에서 강력한 반전 신호가 감지되면, LTF(Low Time Frame)의 방향과 무관하게 포지션 방향을 강제로 'None'으로 전환합니다 (최상위 리스크 관리).
        // 0~1 범위에서 실전에서는 0.6 근방이 적당해 보임 (0.6은 HTF 반전 신호가 60% 이상 강할 때 이번 봉 방향 차단) >> 고정
        public double HtfReversalThreshold { get; init; } = 0.6;

        // 정상 모드에서 허용하는 최대 포지션 비중.
        // TargetNotional = balance * leverage * weight로 해석된다.
        // 평소 거래 시 설정할 수 있는 포지션 크기의 절대적 상한선입니다.
        public double MaxWeight { get; init; } = 0.3;//0.2;
    }
}