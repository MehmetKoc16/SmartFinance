namespace SmartFinance.Application.DTOs.Investment;

public class PortfolioSummaryDto
{
    public decimal TotalPurchaseValue{get;set;}
    public decimal TotalCurrentValue{get;set;}
    public decimal TotalProfitLoss{get;set;}
    public decimal TotalProfitLossPercentage{get;set;}
    public int TotalInvestmentCount{get;set;}
    public IEnumerable<PortfolioByTypeDto> ByType{get;set;}=new List<PortfolioByTypeDto>();
}

public class PortfolioByTypeDto{
    public string InvestmentType{get;set;}=string.Empty;
    public decimal TotalCurrentValue{get;set;}
    public decimal ProfitLoss{get;set;}
    public int Count{get;set;}
}