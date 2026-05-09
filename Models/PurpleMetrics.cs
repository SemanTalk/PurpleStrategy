namespace PurpleStrategy.Models
{
    /// <summary>
    /// 퍼플 지표 계산 결과 구조체
    /// </summary>
    public class PurpleMetrics
    {
        public DateTime Date { get; set; }

        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public double VolEma { get; set; }
        public double SPb { get; set; }      // s-%b

        public double SMb { get; set; }      // s-%m

        public double Trend { get; set; }    // Purple.Trend

        public double Histogram { get; set; } // Purple.Momentum

        public double ATR { get; set; } // ATR
        public double BBW { get; set; } // 볼린저밴드폭
        public double BBUpper { get; set; } // 볼린저밴드 상단선
        public double BBC { get; set; } // 볼린저밴드 중심선
        public double BBLower { get; set; } // 볼린저밴드 하단선
        public double ShortEma { get; set; } // 단기이평선
        public double LongEma { get; set; } // 장기이평선

        public RegressionResult LRResult { get; set; } // 선형회귀분석

        // 이전 봉 데이터 참조용

        public decimal PrevOpen { get; set; }

        public decimal PPrevOpen { get; set; }

        public decimal PrevClose { get; set; }

        public decimal PPrevClose { get; set; }
        public decimal PrevHigh { get; set; }

        public decimal PPrevHigh { get; set; }
        public decimal PrevLow { get; set; }

        public decimal PPrevLow { get; set; }

        public decimal PrevVolume { get; set; }

        public double PrevVolEma { get; set; }
        public double PPrevVolEma { get; set; }

        public decimal PPrevVolume { get; set; }
        public double PrevBBUpper { get; set; } // 볼린저밴드 상단선

        public double PrevBBW { get; set; } // 볼린저밴드 중심선

        public double PPrevBBW { get; set; } // 볼린저밴드폭
        public double PPrevBBUpper { get; set; } // 볼린저밴드폭

        public double PrevBBC { get; set; } // 볼린저밴드 중심선

        public double PPrevBBC { get; set; } // 볼린저밴드 중심선
        public double PrevBBLower { get; set; } // 볼린저밴드 하단선

        public double PPrevBBLower { get; set; } // 볼린저밴드 하단선

        public double PrevShortEma { get; set; } // 단기이평선
        public double PPrevShortEma { get; set; } // 단기이평선
        public double PrevLongEma { get; set; } // 장기이평선
        public double PPrevLongEma { get; set; } // 장기이평선

        public double PrevSPb { get; set; }

        public double PPrevSPb { get; set; }

        public double PrevSMb { get; set; }

        public double PPrevSMb { get; set; }

        public double PrevTrend { get; set; }

        public double PPrevTrend { get; set; }

        public double PrevHistogram { get; set; }

        public double PPrevHistogram { get; set; }

        public RegressionResult PrevLRResult { get; set; } // 선형회귀분석

        public RegressionResult PPrevLRResult { get; set; } // 선형회귀분석
    }
}