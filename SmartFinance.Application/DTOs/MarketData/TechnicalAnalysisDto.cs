namespace SmartFinance.Application.DTOs.MarketData;

public class TechnicalAnalysisDto
{
    public string Symbol { get; set; } = string.Empty;
    public string InvestmentType { get; set; } = string.Empty;
    public List<PriceBarDto> PriceBars { get; set; } = new();
    public List<IndicatorPointDto> Rsi { get; set; } = new();
    public MacdResultDto Macd { get; set; } = new();
    public BollingerBandsResultDto BollingerBands { get; set; } = new();
}
