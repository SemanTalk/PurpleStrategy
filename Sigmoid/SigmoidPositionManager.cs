using PurpleStrategy.Models;

namespace PurpleStrategy.Sigmoid
{
    // [2026-05-14] HTF Trend Follow Pure 재구축.
    // 시그모이드 + 파라미터 + 리스크 스캐폴드 (MDD 방어 / directFlip / rate limit / ExitSpeed) 만 보존.
    // Confirmation/Heat/Energy/Reversal/HTF reversal block 전부 제거.
    public class SigmoidPositionManager
    {
        public SigmoidParams Params { get; init; } = new();

        // ── 상태 ─────────────────────────────────────────────────────────
        private double _currentWeight = 0;
        private decimal _peakBalance = 0;
        private bool _inDefenseMode = false;
        private int _heldDir = 0;
        private string _currentSide = "None";

        // ── 읽기 전용 접근자
        public double CurrentWeight => _currentWeight;
        public string CurrentSide => _currentSide;
        public int PrevDir => _heldDir;
        public bool InDefense => _inDefenseMode;

        // ── 주문 액션
        public enum OrderAction
        {
            Hold,
            IncreasePosition,
            DecreasePosition,
            CloseAll,
            CloseAndReverse,
        }

        public class OrderSide
        {
            public const string Long = "Long";
            public const string Short = "Short";
            public const string None = "None";
        }

        /// <summary>
        /// 매 봉 확정 시 반환되는 주문 지시. 호출부는 SigmoidStrategyRunner.ExecuteInstruction.
        /// </summary>
        public record OrderInstruction(
            OrderAction Action,
            string Side,
            double MaxWeight,
            double TargetWeight,
            double CurrentWeight,
            double WeightDelta,
            decimal TargetNotional,
            // HTF Trend (신호 소스). htf 없으면 ltf.Trend fallback.
            double Trend,
            // CalcSignal 이 반환한 정규화 강도 0~1 (Sigmoid 입력 전).
            double Strength,
            double Leverage,
            string Message
        );

        // ══════════════════════════════════════════════════════════════════
        // 메인 메서드 — 매 봉 확정 시 호출
        // ══════════════════════════════════════════════════════════════════

        public OrderInstruction OnBarClose(
             PurpleMetrics ltf,
             PurpleMetrics? htf,
             decimal balance,
             double leverage = 1)
        {
            // 1) MDD 방어 모드 갱신
            if (balance > _peakBalance) _peakBalance = balance;
            double equityRatio = _peakBalance > 0 ? (double)(balance / _peakBalance) : 1.0;
            double currentDD = 1.0 - equityRatio;
            if (!_inDefenseMode && currentDD >= Params.MddDefenseThreshold)
                _inDefenseMode = true;
            else if (_inDefenseMode && currentDD < Params.MddDefenseExitThreshold)
                _inDefenseMode = false;
            double activeMaxWeight = _inDefenseMode ? Params.MddDefenseMaxWeight : Params.MaxWeight;

            // 2) 신호 산출 — HTF Trend Follow Pure
            var signal = SigmoidHelper.CalcSignal(htf, Params);
            int dir = signal.Dir;
            double strength = signal.Strength;
            double trendForLog = htf?.Trend ?? ltf.Trend;

            string desiredSide = dir > 0 ? OrderSide.Long : dir < 0 ? OrderSide.Short : OrderSide.None;
            bool directFlip = dir != 0 && dir != _heldDir && _heldDir != 0;

            // 3) directFlip 처리
            if (directFlip && _currentWeight > 0.001)
            {
                double prevWeight = _currentWeight;
                string closingSide = _currentSide;
                _currentWeight = 0;
                _heldDir = dir;
                _currentSide = OrderSide.None;

                return new OrderInstruction(
                    Action: OrderAction.CloseAll,
                    Side: closingSide,
                    MaxWeight: activeMaxWeight,
                    TargetWeight: 0,
                    CurrentWeight: 0,
                    WeightDelta: -prevWeight,
                    TargetNotional: 0,
                    Trend: trendForLog,
                    Strength: strength,
                    Leverage: leverage,
                    Message: $"{closingSide} 전량 청산. 다음 봉부터 {desiredSide} 진입 시도."
                );
            }

            if (directFlip && _currentWeight <= 0.001)
            {
                _heldDir = dir;
                _currentSide = OrderSide.None;
                return new OrderInstruction(
                    Action: OrderAction.Hold,
                    Side: OrderSide.None,
                    MaxWeight: activeMaxWeight,
                    TargetWeight: 0,
                    CurrentWeight: 0,
                    WeightDelta: 0,
                    TargetNotional: 0,
                    Trend: trendForLog,
                    Strength: strength,
                    Leverage: leverage,
                    Message: $"Flip(zero) → {desiredSide} 방향 전환. 다음 봉부터 진입."
                );
            }

            // 4) 목표 비중 산출
            // strength ∈ [0,1] → Sigmoid(strength × Steepness) ∈ (0.5, 1) (strength>0) → (sig-0.5)*2 ∈ [0,1)
            // dir=0 이면 즉시 0 (휴식).
            double sig = SigmoidHelper.Sigmoid(strength * Params.Steepness);
            double targetWeight = dir == 0 ? 0 : activeMaxWeight * Math.Max(0.0, (sig - 0.5) * 2.0);

            // 5) 한 봉당 비중 변화 제한
            double delta = targetWeight - _currentWeight;
            double maxChange = delta < 0
                ? Params.MaxWeightChangePerBar * Params.ExitSpeedMultiplier
                : Params.MaxWeightChangePerBar;
            if (Math.Abs(delta) > maxChange)
                delta = Math.Sign(delta) * maxChange;

            double newWeight = Math.Max(0, Math.Min(activeMaxWeight, _currentWeight + delta));
            double actualDelta = newWeight - _currentWeight;

            OrderAction action;
            string message;
            string orderSide = desiredSide;

            // 6) 주문 액션 결정
            if (_currentWeight < 0.001 && newWeight < 0.001)
            {
                action = OrderAction.Hold;
                message = dir == 0
                    ? "No trend (gate fail), Hold"
                    : $"Wait weight entry (Strength: {strength:F2})";
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
                message = $"Increase: {_currentWeight:P1} → {newWeight:P1} (Trend:{trendForLog:+0.0000;-0.0000;0.0000}, Str:{strength:F2})";
                orderSide = _currentWeight > 0.001 ? _currentSide : desiredSide;
            }
            else if (actualDelta < -1e-9)
            {
                action = OrderAction.DecreasePosition;
                message = $"Decrease: {_currentWeight:P1} → {newWeight:P1} (Signal weakening)";
                orderSide = _currentSide;
            }
            else
            {
                action = OrderAction.Hold;
                message = $"Hold: {_currentWeight:P1} (Δ{actualDelta:+0.000;-0.000})";
                orderSide = _currentSide;
            }

            decimal targetNotional = balance * (decimal)leverage * (decimal)newWeight;

            // 7) 내부 상태 갱신
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
                Trend: trendForLog,
                Strength: strength,
                Leverage: leverage,
                Message: message
            );
        }

        // ══════════════════════════════════════════════════════════════════
        //  상태 관리
        // ══════════════════════════════════════════════════════════════════

        public void Reset()
        {
            _currentWeight = 0;
            _heldDir = 0;
            _currentSide = "None";
            _peakBalance = 0;
            _inDefenseMode = false;
        }

        /// <summary>
        /// 외부에서 포지션 동기화 (재시작 시 기존 포지션 반영)
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

        public System.Drawing.Color ActionColor(SigmoidPositionManager.OrderAction action) => action switch
        {
            SigmoidPositionManager.OrderAction.IncreasePosition => System.Drawing.Color.Green,
            SigmoidPositionManager.OrderAction.DecreasePosition => System.Drawing.Color.Orange,
            SigmoidPositionManager.OrderAction.CloseAll => System.Drawing.Color.Tomato,
            SigmoidPositionManager.OrderAction.CloseAndReverse => System.Drawing.Color.DeepPink,
            _ => System.Drawing.Color.Silver,
        };

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
