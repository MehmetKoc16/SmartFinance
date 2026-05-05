namespace SmartFinance.Application.DTOs.Investment;

public class CreateInvestmentDto{
    public string Name{get;set;}=string.Empty;
    public string FullName{get;set;}=string.Empty;
    public decimal PurchasePrice{get;set;}
    public decimal CurrentPrice{get;set;}
    public double Quantity{get;set;}
    public string InvestmentType{get;set;}=string.Empty;
}