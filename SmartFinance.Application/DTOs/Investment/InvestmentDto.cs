namespace SmartFinance.Application.DTOs.Investment;

public class InvestmentDto
{
    public int Id {get;set;}
     public string Name {get;set;} = string.Empty;
    public string FullName {get;set;} = string.Empty;
    public decimal PurchasePrice {get;set;}
    public decimal CurrentPrice {get;set;}
    public double Quantity{get;set;}
    public string InvestmentType{get;set;} = string.Empty;
    public DateTime CreatedDate {get;set;}

    public decimal TotalPurchaseValue=>PurchasePrice*(decimal)Quantity;
    public decimal TotalCurrentValue => CurrentPrice*(decimal)Quantity;
    public decimal ProfitLoss => TotalCurrentValue-TotalPurchaseValue;
    public decimal ProfitLossPercentage => TotalPurchaseValue == 0 ? 0 : Math.Round((ProfitLoss/TotalPurchaseValue)*100,2);

}