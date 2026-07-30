namespace SmartFinance.Application.DTOs.MarketData;

public class TechnicalAnalysisDto
{
    public string Symbol { get; set; } = string.Empty;
    public string InvestmentType { get; set; } = string.Empty;
    public List<PriceBarDto> PriceBars { get; set; } = new();
    public List<IndicatorSeriesDto> Indicators { get; set; } = new();
    public StockStatisticsDto? Statistics { get; set; }
}
