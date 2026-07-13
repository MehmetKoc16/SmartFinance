namespace SmartFinance.Application.DTOs.Investment;

public class PriceRefreshErrorDto
{
    public int InvestmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public class RefreshPricesResultDto
{
    public int UpdatedCount { get; set; }
    public int FailedCount { get; set; }
    public List<InvestmentDto> Investments { get; set; } = new();
    public List<PriceRefreshErrorDto> Errors { get; set; } = new();
}
