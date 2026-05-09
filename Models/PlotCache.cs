using CryptoFuturesBacktester.Core.Quotes;
using Skender.Stock.Indicators;

namespace PurpleStrategy.Models
{
    public class PlotCache
    {
        // 실시간 업데이트용 — Y/X 배열 참조 (ScatterLine이 동일 인스턴스 보관)
        public double[]? MACDDataX { get; set; }

        public double[]? MACDDataY { get; set; }
        public double[]? SMBDataX { get; set; }
        public double[]? SMBDataY { get; set; }
        public double[]? SPBDataX { get; set; }
        public double[]? SPBDataY { get; set; }

        // 데이터 결과 보관
        public List<FuturesQuote> mQuoteResults { get; set; } = [];

        public List<EmaResult>? mShortEmaResults = null;
        public List<EmaResult>? mLongEmaResults = null;
        public List<EmaResult>? mVolEmaResults = null;
        public List<MacdResult>? mMacdResuts = null;
        public List<EmaResult>? mSMbResults = null;
        public List<EmaResult>? mSPbResults = null;
        public List<BollingerBandsResult>? mBollingerBandsResults = null;

        /// <summary>
        /// 차트에서 관리 중인 모든 플롯 객체를 제거하고 데이터를 초기화합니다.
        /// </summary>
        public void Clear()
        {
            mQuoteResults.Clear();
            mShortEmaResults?.Clear();
            mLongEmaResults?.Clear();
            mVolEmaResults?.Clear();
            mMacdResuts?.Clear();
            mSMbResults?.Clear();
            mSPbResults?.Clear();
            mBollingerBandsResults?.Clear();
        }
    }
}