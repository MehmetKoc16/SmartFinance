namespace SmartFinance.Application.DTOs.MarketData;

public class BollingerBandPointDto
{
    public DateTime Date { get; set; }
    public decimal? UpperBand { get; set; }
    public decimal? MiddleBand { get; set; }
    public decimal? LowerBand { get; set; }
}

public class BollingerBandsResultDto
{
    public List<BollingerBandPointDto> Points { get; set; } = new();
}
