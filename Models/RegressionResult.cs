namespace PurpleStrategy.Models
{
    public struct RegressionResult
    {
        public double Slope;        // 기울기: 추세의 방향 (양수=상승, 음수=하락)
        public double Intercept;    // 절편: 시작점 지지/저항 확인용
        public double StdDev;       // 표준편차: 채널의 폭 (변동성 범위)
        public double RSquared;     // 결정계수: 추세의 강도/신뢰도 (0~1, 1에 가까울수록 강력)
        public double CurrentBasis; // 현재 시점의 회귀선상 가격 (중심값)
    }
}