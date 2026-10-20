using SmartFinance.Application.DTOs.Transaction;

namespace SmartFinance.Application.Interfaces;

public interface ITransactionService{
    Task<object> GetFilteredTransactionsAsync(TransactionFilterDto filter);
    Task<IEnumerable<TransactionDto>> GetAllTransactionsAsync();
    Task<TransactionDto?> GetTransactionByIdAsync(int id);
    Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto);
    Task UpdateTransactionAsync(int id, UpdateTransactionDto dto);
    Task DeleteTransactionAsync(int id);
    Task<IReadOnlyList<DeletedTransactionDto>> GetDeletedTransactionsAsync();
    Task<TransactionDto?> RestoreTransactionAsync(int id);
    Task<IReadOnlyList<CategorySummaryDto>> GetCategorySummaryAsync(int month, int year, int type = 2);
    Task<MonthlySummaryDto> GetMonthlySummaryAsync(int month, int year);
}
