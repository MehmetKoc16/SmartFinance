namespace SmartFinance.Application.DTOs.MarketData;

public class PriceQuoteDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime AsOf { get; set; }
}
