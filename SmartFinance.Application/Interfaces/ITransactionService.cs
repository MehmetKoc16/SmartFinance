using SmartFinance.Application.DTOs.Transaction;

namespace SmartFinance.Application.Interfaces;

public interface ITransactionService{
    Task<IEnumerable<TransactionDto>> GetAllTransactionsAsync();
    Task<TransactionDto> GetTransactionByIdAsync(int id);
    Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto);
    Task UpdateTransactionAsync(int id, CreateTransactionDto dto);
    Task DeleteTransactionAsync(int id);
}