using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class TransactionService : ITransactionService
{
    private readonly IGenericRepository<Transaction> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TransactionService(IGenericRepository<Transaction> repository, SmartFinanceDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _repository = repository;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<IEnumerable<TransactionDto>> GetAllTransactionsAsync()
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transactions = await _repository.GetAllAsync();
        return transactions.Where(t=>t.UserId==userId).Select(t => new TransactionDto
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
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transaction = await _repository.GetByIdAsync(id);
        if (transaction == null || transaction.UserId!=userId) return null;
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
        // Token'dan UserId al
        var userId = int.Parse(_httpContextAccessor.HttpContext!.User
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var transaction = new Transaction
        {
            Amount = dto.Amount,
            Description = dto.Description,
            TransactionDate = dto.TransactionDate,
            Type = dto.Type,
            CategoryId = dto.CategoryId,
            UserId = userId
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
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transaction = await _repository.GetByIdAsync(id);
        if(transaction == null || transaction.UserId!=userId)
            throw new NotFoundException("İşlem bulunamadı!");

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
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transaction = await _repository.GetByIdAsync(id);
        if(transaction == null || transaction.UserId!=userId)
            throw new NotFoundException("İşlem bulunamadı!");
        transaction.IsDeleted = true;
        transaction.UpdatedDate = DateTime.UtcNow;
        _repository.Update(transaction);
        await _context.SaveChangesAsync();
    }
}