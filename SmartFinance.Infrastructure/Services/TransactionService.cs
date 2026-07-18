using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
    public async Task<object> GetFilteredTransactionsAsync(TransactionFilterDto filter)
    {
        var userId= int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transactions=await _repository.GetAllAsync();

        var query=transactions.Where(t=>t.UserId==userId);

        if(filter.StartDate.HasValue)
            query=query.Where(t=>t.TransactionDate>=filter.StartDate.Value);
        if(filter.EndDate.HasValue)
            query=query.Where(t=>t.TransactionDate<=filter.EndDate.Value);

        if(filter.Type.HasValue)
            query=query.Where(t=>(int)t.Type==filter.Type.Value);

        if(filter.CategoryId.HasValue)
            query=query.Where(t=>t.CategoryId==filter.CategoryId.Value);

        var totalCount=query.Count();

        var items = query.Skip((filter.Page-1)*filter.PageSize).Take(filter.PageSize).Select(t=>new TransactionDto
        {
            Id=t.Id,
            UserId=t.UserId,
            Amount=t.Amount,
            Description=t.Description,
            MerchantName=t.MerchantName,
            TransactionDate=t.TransactionDate,
            Type=t.Type,
            CategoryId=t.CategoryId,
            CreatedDate=t.CreatedDate,
        });

        return new{
            items=items,
            totalCount=totalCount,
            page=filter.Page,
            pageSize=filter.PageSize,
            totalPages=(int)Math.Ceiling((double)totalCount/filter.PageSize)
        };
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
            MerchantName = t.MerchantName,
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
            MerchantName = transaction.MerchantName,
            TransactionDate = transaction.TransactionDate,
            Type = transaction.Type,
            CategoryId = transaction.CategoryId,
            CreatedDate = transaction.CreatedDate,
        };
    }

    private async Task EnsureCategoryOwnedAsync(int? categoryId, int userId)
    {
        if (!categoryId.HasValue) return;
        var owned = await _context.Categories.AnyAsync(c => c.Id == categoryId.Value && c.UserId == userId);
        if (!owned)
            throw new BadRequestException("Geçersiz kategori!");
    }

    public async Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto)
    {
        // Token'dan UserId al
        var userId = int.Parse(_httpContextAccessor.HttpContext!.User
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        await EnsureCategoryOwnedAsync(dto.CategoryId, userId);

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
            MerchantName = transaction.MerchantName,
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

        await EnsureCategoryOwnedAsync(dto.CategoryId, userId);

        transaction.Amount = dto.Amount;
        transaction.Description = dto.Description;
        // Elle düzenleme, otomatik çıkarılmış işyeri adını geçersiz kılar —
        // arayüz artık kullanıcının az önce yazdığı Description'ı esas alsın.
        transaction.MerchantName = null;
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

    public async Task<MonthlySummaryDto> GetMonthlySummaryAsync(int month, int year)
    {
        var userId=int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var transactions=await _repository.GetAllAsync();
        var monthly = transactions.Where(t=>t.UserId==userId && t.TransactionDate.Month==month && t.TransactionDate.Year==year);

        var totalIncome=monthly.Where(t=>t.Type==TransactionType.Income).Sum(t=>t.Amount);

        var totalExpense=monthly.Where(t=>t.Type==TransactionType.Expense).Sum(t=>t.Amount);

        return new MonthlySummaryDto{
            TotalIncome=totalIncome,
            TotalExpense=totalExpense,
            Balance=totalIncome-totalExpense,
            TransactionCount=monthly.Count(),
            Month=month,
            Year=year
        };
    } 
}