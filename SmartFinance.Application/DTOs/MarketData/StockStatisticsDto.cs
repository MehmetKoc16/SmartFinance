namespace SmartFinance.Application.DTOs.MarketData;

// Sadece hisse senedi (stock) tipi yatirimlar icin doldurulur — Yahoo Finance'in
// quoteSummary uc noktasindan gelir. Taban/Tavan ve Ihracat orani bilerek yok:
// Yahoo'da karsiligi bulunmuyor, yanlis/yaklasik veri gostermektense hic gosterilmiyor.
public class StockStatisticsDto
{
    public decimal? Open { get; set; }
    public decimal? PreviousClose { get; set; }
    public decimal? DayHigh { get; set; }
    public decimal? DayLow { get; set; }
    public decimal? FiftyTwoWeekHigh { get; set; }
    public decimal? FiftyTwoWeekLow { get; set; }
    public decimal? AverageVolume { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? TrailingPE { get; set; }
    public decimal? PriceToBook { get; set; }
    public decimal? EquityValue { get; set; }
    public decimal? ReturnOnEquity { get; set; }
    public decimal? Ebitda { get; set; }
    public decimal? ProfitMargin { get; set; }
    public decimal? GrossMargin { get; set; }
}
