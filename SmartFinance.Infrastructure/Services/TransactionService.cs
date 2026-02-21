using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

public class TransactionService : ITransactionService
{
    private readonly IGenericRepository<Transaction> _repository;
    private readonly SmartFinanceDbContext _context;

    public TransactionService(IGenericRepository<Transaction> repository, SmartFinanceDbContext context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<IEnumerable<TransactionDto>> GetAllTransactionsAsync()
    {
        var transactions = await _repository.GetAllAsync();
        return transactions.Select(t => new TransactionDto
        {
            Id = t.Id,
            UserId = t.UserId,
            Amount = t.Amount,
            Description = t.Description,
            TransactionDate = t.TransactionDate,
            Type = t.Type,
            CategoryId = t.CategoryId,
            CreatedDate = t.CreatedDate,
        });
    }

    public async Task<TransactionDto?> GetTransactionByIdAsync(int id)
    {
        var transaction = await _repository.GetByIdAsync(id);
        if (transaction == null) return null;
        return new TransactionDto
        {
            Id = transaction.Id,
            UserId = transaction.UserId,
            Amount = transaction.Amount,
            Description = transaction.Description,
            TransactionDate = transaction.TransactionDate,
            Type = transaction.Type,
            CategoryId = transaction.CategoryId,
            CreatedDate = transaction.CreatedDate,
        };
    }

    public async Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto)
    {
        var transaction = new Transaction
        {
            Amount = dto.Amount,
            Description = dto.Description,
            TransactionDate = dto.TransactionDate,
            Type = dto.Type,
            CategoryId = dto.CategoryId,
            UserId = 1 // Geçici: JWT eklenince kaldırılacak
        };

        await _repository.AddAsync(transaction);
        await _context.SaveChangesAsync();

        return new TransactionDto
        {
            Id = transaction.Id,
            UserId = transaction.UserId,
            Amount = transaction.Amount,
            Description = transaction.Description,
            TransactionDate = transaction.TransactionDate,
            Type = transaction.Type,
            CategoryId = transaction.CategoryId,
            CreatedDate = transaction.CreatedDate,
        };
    }

    public async Task UpdateTransactionAsync(int id, CreateTransactionDto dto)
    {
        var transaction = await _repository.GetByIdAsync(id);
        transaction.Amount = dto.Amount;
        transaction.Description = dto.Description;
        transaction.TransactionDate = dto.TransactionDate;
        transaction.Type = dto.Type;
        transaction.CategoryId = dto.CategoryId;
        transaction.UpdatedDate = DateTime.UtcNow;
        _repository.Update(transaction);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteTransactionAsync(int id)
    {
        var transaction = await _repository.GetByIdAsync(id);
        transaction.IsDeleted = true;
        transaction.UpdatedDate = DateTime.UtcNow;
        _repository.Update(transaction);
        await _context.SaveChangesAsync();
    }
}