namespace SmartFinance.Application.DTOs.Transaction;

public class DeletedTransactionDto : TransactionDto
{
    public DateTime DeletedAt { get; set; }
}
