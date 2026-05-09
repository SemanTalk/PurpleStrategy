using CryptoFuturesBacktester.Core.Quotes;
using Skender.Stock.Indicators;

namespace PurpleStrategy.Utils
{
    public static class Commons
    {
        public static double SlopeToDeg(double slope) =>
           Math.Atan(slope) * (180.0 / Math.PI);

        /// <summary>
        /// Bollinger Band Width 정규화 (bbResults 자체의 Width 속성에 0~100 값으로 갱신)
        /// </summary>
        public static void UpdateNormalizedBbw(ref List<BollingerBandsResult> bbResults, int lookbackPeriod = 120)
        {
            var list = bbResults.ToList();
            var rawWidths = list.Select(x => (double)(x.Width ?? 0)).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                if (i < lookbackPeriod)
                {
                    list[i].Width = 50; // 초기 데이터 부족 시 중간값
                    continue;
                }

                var window = rawWidths.GetRange(i - lookbackPeriod, lookbackPeriod);
                double min = window.Min();
                double max = window.Max();

                if (Math.Abs(max - min) < 1e-10)
                {
                    list[i].Width = 50;
                }
                else
                {
                    double norm = (rawWidths[i] - min) / (max - min) * 100;
                    list[i].Width = (double?)Math.Clamp(norm, 0, 100);
                }
            }
        }

        // 특정 인덱스가 스윙 하이인지 확인하는 함수 (좌우 n개 기준)
        public static bool IsSwingHigh(List<FuturesQuote> quotes, int index, int length)
        {
            if (index < length || index >= quotes.Count - length) return false;
            for (int i = 1; i <= length; i++)
            {
                if (quotes[index].High < quotes[index - i].High ||
                    quotes[index].High < quotes[index + i].High) return false;
            }
            return true;
        }

        // 특정 인덱스가 스윙 로우인지 확인하는 함수
        public static bool IsSwingLow(List<FuturesQuote> quotes, int index, int length)
        {
            if (index < length || index >= quotes.Count - length) return false;
            for (int i = 1; i <= length; i++)
            {
                if (quotes[index].Low > quotes[index - i].Low ||
                    quotes[index].Low > quotes[index + i].Low) return false;
            }
            return true;
        }
    }
}