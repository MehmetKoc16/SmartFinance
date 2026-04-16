namespace SmartFinance.Application.DTOs.Transaction;

public class MonthlySummaryDto{
    public decimal TotalIncome{get;set;}
    public decimal TotalExpense{get;set;}
    public decimal Balance{get;set;}
    public int TransactionCount{get;set;}
    public int Month{get;set;}
    public int Year{get;set;}
}