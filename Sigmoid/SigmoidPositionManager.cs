using CryptoFuturesBacktester.Core.Quotes;
using PurpleStrategy.Models;
using PurpleStrategy.Utils;

namespace PurpleStrategy.Sigmoid
{
    public class SigmoidPositionManager
    {
        public SigmoidParams Params { get; init; } = new();

        // ── 상태 ─────────────────────────────────────────────────────────
        // 현재 전략이 목표로 관리 중인 포지션 비중(0~MaxWeight).
        // 실제 체결 수량은 호출부(SigmoidWeightStrategy)가 이 비중을 기준으로 맞춘다.
        // 현재 시스템이 유지하고 있는 실제 포지션 웨이트 (0~1 사이의 값). 이 값이 모든 주문 결정의 기초가 됩니다.
        private double _currentWeight = 0;

        // MDD 방어 모드 판단용 최고 잔고와 방어 상태.
        // balance가 peak 대비 크게 낮아지면 activeMaxWeight를 MddDefenseMaxWeight로 낮춘다.
        // 전략을 시작한 이후 달성했던 최대 자산 규모 (Max Balance). Drawdown 계산에 사용됩니다.
        private decimal _peakBalance = 0;

        // 현재 시스템이 MDD 방어 모드인지 여부 (Boolean).
        private bool _inDefenseMode = false;

        // 마지막으로 보유/선택한 방향: 1=Long, -1=Short, 0=None.
        // _currentSide는 주문 실행부와 로그에서 쓰는 문자열 표현이다.
        // 현재 포지션의 방향 (1: Long, -1: Short, 0: None). 이전 봉에서 결정된 유지 방향입니다.
        private int _heldDir = 0;

        // 현재 포지션의 시장 측면 ("Long", "Short", "None").
        private string _currentSide = "None";

        // ── 읽기 전용 접근자

        public double CurrentWeight => _currentWeight;
        public string CurrentSide => _currentSide;
        public int PrevDir => _heldDir;
        public bool InDefense => _inDefenseMode;

        // ── 주문 액션

        public enum OrderAction
        {
            Hold,              // 변화 없음
            IncreasePosition,  // 같은 방향 증량
            DecreasePosition,  // 같은 방향 감량
            CloseAll,          // 전량 청산 (웨이트→0)
            CloseAndReverse,   // 전량 청산 후 다음 봉 반대 방향 시작
        }

        public class OrderSide
        {
            public const string Long = "Long";
            public const string Short = "Short";
            public const string None = "None";
        }

        /// <summary>
        /// 매 봉 확정 시 반환되는 주문 지시
        /// 이 record는 "무엇을 주문할지"와 "왜 그런 판단을 했는지"를 호출부에 전달하는 DTO다.
        /// 실제 주문 실행은 SigmoidWeightStrategy.ExecuteInstruction()에서 처리한다.
        /// </summary>
        public record OrderInstruction(
            // 수행해야 할 행동: Hold, IncreasePosition, DecreasePosition, CloseAll, CloseAndReverse
            OrderAction Action,
            // 거래 방향 : "Long", "Short", "None"
            string Side,
            // 전략이 목표로 하는 최대 웨이트 (0~1)
            double MaxWeight,
            // 전략이 목표로 하는 웨이트 (0~1)
            double TargetWeight,
            // 현재/변화된 실제 웨이트: 현재 웨이트 (변화 제한 적용 후)
            double CurrentWeight,
            // 현재/변화된 차이: 이번 봉의 웨이트 변화량
            double WeightDelta,
            // 목표 포지션 크기 (USDT)
            decimal TargetNotional,
            // Optimized Score
            double FinalScore,
            // Trend 크기
            double Trend,
            // 순 신호 강도 (디버깅용)
            double NetSignal,
            // Confirmation 점수
            double ConfirmScore,
            // Heat 점수
            double HeatScore,
            // Energy raw (0~W_Energy)
            double EnergyScore,
            // 레버리지
            double Leverage,
            // 반전 구간 강도 0~1 (0.4↑=차단, 0.6↑=억제)
            double ReversalStrength,
            // 상태 설명
            string Message
        );

        // ══════════════════════════════════════════════════════════════════
        // 메인 메서드 — 매 봉 확정 시 호출: 주문 결정 메인 로직
        // 이 함수는 시스템의 심장이며, 한 봉(캔들) 마감마다 호출됩니다.
        // ══════════════════════════════════════════════════════════════════

        public OrderInstruction OnBarClose(
            PurpleMetrics ltf,
            PurpleMetrics? htf,
            decimal balance,
            double leverage = 1,
            FuturesQuote? liveHtfFuturesQuote = null)
        {
            /*
            // 현재 LTF 종가가 진행 중인 HTF 봉의 범위 내에 있는지
            bool insideHtfRange = bar.Quote.Close >= liveHtf.Low
                               && bar.Quote.Close <= liveHtf.High;

            // 진행 중인 HTF 봉이 완성봉 고점을 돌파하는지 (실시간 판단)
            bool breakingOut = liveHtf.High > completedHtf.High;
            */

            // 1) MDD 방어 모드 갱신
            // peakBalance는 관측된 최고 balance이고, currentDD는 최고점 대비 현재 낙폭이다.
            // 방어 모드에서는 신규 목표 비중 상한을 MaxWeight가 아니라 MddDefenseMaxWeight로 낮춘다.
            // 현재 자산(balance)을 최고 자산(_peakBalance)과 비교하여 Drawdown(DD)을 계산합니다.
            // DD≥MddDefenseThreshold 이면 → _inDefenseMode = true(위험 관리 모드 진입).
            // DD<MddDefenseExitThreshold 이면 → _inDefenseMode = false(정상 모드로 복귀).
            // 이 모드에 따라 적용되는 최대 웨이트(activeMaxWeight)가 달라집니다.
            if (balance > _peakBalance) _peakBalance = balance;
            double equityRatio = _peakBalance > 0 ? (double)(balance / _peakBalance) : 1.0;
            double currentDD = 1.0 - equityRatio;
            if (!_inDefenseMode && currentDD >= Params.MddDefenseThreshold)
                _inDefenseMode = true;
            else if (_inDefenseMode && currentDD < Params.MddDefenseExitThreshold)
                _inDefenseMode = false;
            double activeMaxWeight = _inDefenseMode ? Params.MddDefenseMaxWeight : Params.MaxWeight;

            // 2) 점수 계산
            // bd는 LTF Conviction의 하위 점수이고, finalScore는 LTF/HTF를 합성한 표시용 최종 점수다.
            // 실제 비중 산식에는 bd.Confirmation/Trend/Alignment/Energy와 별도 netSignal이 사용된다.
            var bd = PurpleScoringEngine.GetBreakdown(ltf);
            var finalScore = PurpleScoringEngine.GetPurpleFinalScore(ltf, htf);

            // 3) 방향 산출
            // dir: 1=Long, -1=Short, 0=방향 없음. HTF가 있으면 HTF 기준으로 방향을 정한다.
            int dir = SigmoidHelper.CalcDir1(ltf, htf, Params, finalScore);

            // 4) HTF 반전 차단
            // HTF 자체가 현재 보유 방향(_heldDir)에 대해 강한 반전 조짐을 보이면 이번 봉 방향을 없앤다.
            // heldDir이 0이면 CalcReversalStrength가 0을 반환하므로 차단되지 않는다.
            // HtfReversalThreshold가 활성화되고,HTF에서 반전 신호가 감지되면,
            // LTF의 방향과 관계없이 dir을 강제로 0(None)으로 설정합니다. (매우 강력한 리스크 관리 로직)
            if (Params.HtfReversalThreshold > 0 && dir != 0 && htf != null)
            {
                double htfRevStr = SigmoidHelper.CalcReversalStrength(htf, null, _heldDir, Params.MinTrendMagnitude);
                if (htfRevStr >= Params.HtfReversalThreshold)
                    dir = 0;
            }

            string desiredSide = dir > 0 ? OrderSide.Long : dir < 0 ? OrderSide.Short : OrderSide.None;
            bool directFlip = dir != 0 && dir != _heldDir && _heldDir != 0;

            // Confirmation/Heat를 0~1 근사값으로 정규화한다.
            // 주의: ConfirmFullScale/HeatFullScale이 0이면 NaN/Infinity가 발생할 수 있다.
            double confirmNorm = Math.Min(1.0, bd.Confirmation / Params.ConfirmFullScale);
            double heatRaw = bd.Trend + bd.Alignment;
            double heatNorm = Math.Min(1.0, heatRaw / Params.HeatFullScale);

            // 5) 직접 반전 처리

            if (directFlip && _currentWeight > 0.001)
            {
                double prevWeight = _currentWeight;
                string closingSide = _currentSide;
                _currentWeight = 0;
                _heldDir = 0;  // 리셋: _heldDir=newDir 유지 시 다음 봉 CalcReversalStrength가 0을 반환하지 않아 targetWeight=0으로 차단되는 문제 방지
                _currentSide = OrderSide.None;

                return new OrderInstruction(
                    Action: OrderAction.CloseAll,
                    Side: closingSide,
                    MaxWeight: activeMaxWeight,
                    TargetWeight: 0,
                    CurrentWeight: 0,
                    WeightDelta: -prevWeight,
                    TargetNotional: 0,
                    FinalScore: finalScore,
                    Trend: ltf.Trend,
                    NetSignal: 0,
                    ConfirmScore: confirmNorm,
                    HeatScore: heatNorm,
                    EnergyScore: bd.Energy / PurpleScoringEngine.GetWEnergy(),
                    Leverage: leverage,
                    ReversalStrength: 0,
                    Message: $"{closingSide} 전량 청산. 다음 봉부터 {desiredSide} 진입 시도."
                );
            }

            if (directFlip && _currentWeight <= 0.001)
            {
                _heldDir = 0;  // 리셋: Case 1과 동일한 이유로 반전 억제 방지
                _currentSide = OrderSide.None;
                return new OrderInstruction(
                    Action: OrderAction.Hold,
                    Side: OrderSide.None,
                    MaxWeight: activeMaxWeight,
                    TargetWeight: 0,
                    CurrentWeight: 0,
                    WeightDelta: 0,
                    TargetNotional: 0,
                    FinalScore: finalScore,
                    Trend: ltf.Trend,
                    NetSignal: 0,
                    ConfirmScore: confirmNorm,
                    HeatScore: heatNorm,
                    EnergyScore: bd.Energy / PurpleScoringEngine.GetWEnergy(),
                    Leverage: leverage,
                    ReversalStrength: 0,
                    Message: $"Flip(zero) → {desiredSide} 방향 전환. 다음 봉부터 진입."
                );
            }

            // 6) 모멘텀 보너스
            // Histogram 부호가 진입 방향과 같을 때만 bd.Momentum 일부를 netSignal에 더한다.
            double momBoost = 0;
            if (dir != 0 && Params.MomentumBoost > 0)
            {
                bool histAligned = (dir > 0 && ltf.Histogram > 0) ||
                                   (dir < 0 && ltf.Histogram < 0);
                if (histAligned)
                    momBoost = Params.MomentumBoost * Math.Min(1.0, bd.Momentum / 20.0);
            }

            // 7) 순 신호 강도 계산
            // 초기 신호 강도(netSignal): netSignal = 확인 신호 - 과열 페널티 + 모멘텀 보너스 + 에너지 보너스.
            // 이 값이 Sigmoid 입력이 되어 목표 비중을 결정한다.
            double energyNorm = bd.Energy / PurpleScoringEngine.GetWEnergy();  // 0~1
            double netSignal = confirmNorm - heatNorm * Params.HeatPenalty + momBoost + energyNorm * Params.EnergyBoost;

            // 8) 반전 강도 계산 및 신호 감쇠
            // 보유 방향 기준으로 LTF의 모멘텀 둔화, SPb/SMb 다이버전스, 회귀 기울기 약화를 점수화한다.
            // CalcReversalStrength로 반전 강도를 재측정하고,
            // 이 값이 임계값을 넘으면 netSignal에 페널티를 적용하여 과도한 진입을 억제합니다.
            double reversalStrength = SigmoidHelper.CalcReversalStrength(ltf, htf, _heldDir, Params.MinTrendMagnitude);

            if (reversalStrength >= Params.ReversalSignalThreshold)
                netSignal *= (1.0 - reversalStrength);

            // 9) 목표 비중 산출
            // dir이 없으면 목표는 0. 방향이 있으면 netSignal을 Sigmoid로 변환한 뒤 activeMaxWeight로 스케일링한다.
            // targetWeight ∈ (0, activeMaxWeight): netSignal이 클수록 activeMaxWeight에 근접, 작을수록 0에 근접.
            double targetWeight = dir == 0 ? 0 : activeMaxWeight * SigmoidHelper.Sigmoid(netSignal * Params.Steepness);

            // 반전 차단 구간에서는 목표 비중이 현재 비중보다 커질 수 없게 하여 신규 증량을 막는다.
            // Reversal Block: 반전 신호가 강력하면, targetWeight를 현재 웨이트 이하로 제한하여 포지션 축소를 유도합니다.
            if (reversalStrength >= Params.ReversalBlockThreshold)
                targetWeight = Math.Min(targetWeight, _currentWeight);

            // 10) 한 봉당 비중 변화 제한
            // 증량은 천천히, 감량은 ExitSpeedMultiplier만큼 빠르게 허용한다.
            // Change Limit (Δ): 목표 웨이트와 현재 웨이트의 차이(delta)를 계산한 후, 최대 변화량(maxChange)으로 제한합니다. (리스크 관리)
            double delta = targetWeight - _currentWeight;
            double maxChange = delta < 0 ? Params.MaxWeightChangePerBar * Params.ExitSpeedMultiplier : Params.MaxWeightChangePerBar;
            if (Math.Abs(delta) > maxChange)
                delta = Math.Sign(delta) * maxChange;

            // newWeight: 모든 제한(최대 웨이트, 반전 차단, 변경 속도 제한)을 통과한 최종 포지션 크기
            double newWeight = Math.Max(0, Math.Min(activeMaxWeight, _currentWeight + delta));
            double actualDelta = newWeight - _currentWeight;

            OrderAction action;
            string message;
            string orderSide = desiredSide;

            // 11) 주문 액션 결정
            // 이전 비중과 새 비중의 차이를 기준으로 Hold/Increase/Decrease/CloseAll을 선택한다.
            // orderSide는 신규/증량이면 원하는 방향, 감량/청산이면 현재 보유 방향을 유지한다.
            // actualDelta에 따라 OrderAction (Increase/Decrease/CloseAll/CloseAndReverse/Hold)를 결정하고 메시지를 작성합니다.
            if (_currentWeight < 0.001 && newWeight < 0.001)
            {
                action = OrderAction.Hold;
                message = dir == 0
                    ? "Fall short of trend, Hold"
                    : $"Wait weight entry (Signal: {netSignal:F2})";
                orderSide = "None";
            }
            else if (_currentWeight > 0.001 && newWeight < 0.001)
            {
                action = OrderAction.CloseAll;
                message = $"Weight → 0: Close all ({_currentSide})";
                orderSide = _currentSide;
            }
            else if (actualDelta > 1e-9)
            {
                action = OrderAction.IncreasePosition;
                message = $"Increase Position: {_currentWeight:P1} → {newWeight:P1} (Cfm:{bd.Confirmation:F1}, Heat:{heatRaw:F1})";
                orderSide = _currentWeight > 0.001 ? _currentSide : desiredSide;
            }
            else if (actualDelta < -1e-9)
            {
                action = OrderAction.DecreasePosition;
                message = $"Decrease Position: {_currentWeight:P1} → {newWeight:P1} (Overheating/Signal Weakness)";
                orderSide = _currentSide;
            }
            else
            {
                action = OrderAction.Hold;
                message = $"Hold: {_currentWeight:P1} (Δ{actualDelta:+0.000;-0.000})";
                orderSide = _currentSide;
            }

            decimal targetNotional = balance * (decimal)leverage * (decimal)newWeight;

            // 12) 내부 상태 갱신
            // _currentWeight는 이번 봉에서 관리할 새 목표 비중이고,
            // _heldDir/_currentSide는 다음 봉의 반전 판단과 주문 방향 판단에 사용된다.
            _currentWeight = newWeight;
            if (dir != 0) _heldDir = dir;
            if (newWeight >= 0.001 && orderSide != OrderSide.None)
                _currentSide = orderSide;
            else if (newWeight < 0.001)
                _currentSide = OrderSide.None;

            return new OrderInstruction(
                Action: action,
                Side: orderSide,
                MaxWeight: activeMaxWeight,
                TargetWeight: targetWeight,
                CurrentWeight: newWeight,
                WeightDelta: actualDelta,
                TargetNotional: targetNotional,
                FinalScore: finalScore,
                Trend: ltf.Trend,
                NetSignal: netSignal,
                ConfirmScore: confirmNorm,
                HeatScore: heatNorm,
                EnergyScore: energyNorm,
                Leverage: leverage,
                ReversalStrength: reversalStrength,
                Message: message
            );
        }

        // ══════════════════════════════════════════════════════════════════
        //  상태 관리
        // ══════════════════════════════════════════════════════════════════

        public void Reset()
        {
            // 전략 상태를 완전히 초기화한다.
            // peakBalance와 defense도 초기화되므로 새 백테스트/새 세션 시작 상태로 돌아간다.
            _currentWeight = 0;
            _heldDir = 0;
            _currentSide = "None";
            _peakBalance = 0;
            _inDefenseMode = false;
        }

        /// <summary>
        /// 외부에서 포지션 동기화 (재시작 시 기존 포지션 반영)
        /// side/weight를 외부 체결 상태에 맞춰 주입해 Manager의 내부 상태와 실제 포지션을 맞춘다.
        /// weight는 정상 모드 MaxWeight로 클램프되며, 방어 모드 상한은 여기서 적용하지 않는다.
        /// </summary>
        public void SyncPosition(string side, double weight)
        {
            _currentSide = side;
            _currentWeight = Math.Max(0, Math.Min(Params.MaxWeight, weight));
            _heldDir = side == "Long" ? 1 : side == "Short" ? -1 : 0;
        }

        // ══════════════════════════════════════════════════════════════════
        //  OrderInstruction → 출력 확장
        // ══════════════════════════════════════════════════════════════════

        // UI/로그 표시용 색상 매핑. 주문 판단에는 영향을 주지 않는다.
        public System.Drawing.Color ActionColor(SigmoidPositionManager.OrderAction action) => action switch
        {
            SigmoidPositionManager.OrderAction.IncreasePosition => System.Drawing.Color.Green,
            SigmoidPositionManager.OrderAction.DecreasePosition => System.Drawing.Color.Orange,
            SigmoidPositionManager.OrderAction.CloseAll => System.Drawing.Color.Tomato,
            SigmoidPositionManager.OrderAction.CloseAndReverse => System.Drawing.Color.DeepPink,
            _ => System.Drawing.Color.Silver,
        };

        // UI/로그 표시용 짧은 액션 라벨. 주문 판단에는 영향을 주지 않는다.
        public string ActionLabel(SigmoidPositionManager.OrderAction action) => action switch
        {
            SigmoidPositionManager.OrderAction.IncreasePosition => "▲ Increase",
            SigmoidPositionManager.OrderAction.DecreasePosition => "▼ Decrease",
            SigmoidPositionManager.OrderAction.CloseAll => "■ Close",
            SigmoidPositionManager.OrderAction.CloseAndReverse => "⇄ Reverse",
            _ => "● Hold",
        };
    }
}