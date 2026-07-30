namespace SmartFinance.Application.DTOs.MarketData;

public class IndicatorSeriesDto
{
    public string Key { get; set; } = string.Empty;
    public List<IndicatorPointDto> Points { get; set; } = new();
}
