namespace SmartFinance.Application.DTOs.MarketData;

public class MacdPointDto
{
    public DateTime Date { get; set; }
    public decimal? Macd { get; set; }
    public decimal? Signal { get; set; }
    public decimal? Histogram { get; set; }
}

public class MacdResultDto
{
    public List<MacdPointDto> Points { get; set; } = new();
}
