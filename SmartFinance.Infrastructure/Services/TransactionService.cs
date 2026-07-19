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
        // Query() henuz calistirilmamis bir IQueryable dondurur — asagidaki .Where()
        // zincirleri belleğe tum tabloyu cekmek yerine SQL'e itilir.
        var query=_repository.Query().Where(t=>t.UserId==userId);

        if(filter.StartDate.HasValue)
            query=query.Where(t=>t.TransactionDate>=filter.StartDate.Value);
        if(filter.EndDate.HasValue)
            query=query.Where(t=>t.TransactionDate<=filter.EndDate.Value);

        if(filter.Type.HasValue)
            query=query.Where(t=>(int)t.Type==filter.Type.Value);

        if(filter.CategoryId.HasValue)
            query=query.Where(t=>t.CategoryId==filter.CategoryId.Value);

        if(!string.IsNullOrWhiteSpace(filter.Search))
        {
            // .Contains() DB collation'ina gore buyuk/kucuk harf duyarli olabiliyor —
            // ToLower() ile karsilastirarak kullanicinin yazdigi harf buyuklugunden bagimsiz hale getiriyoruz.
            var search = filter.Search.ToLower();
            query=query.Where(t=>t.Description.ToLower().Contains(search) ||
                (t.MerchantName != null && t.MerchantName.ToLower().Contains(search)));
        }

        var totalCount=await query.CountAsync();

        var items = await query.Skip((filter.Page-1)*filter.PageSize).Take(filter.PageSize).Select(t=>new TransactionDto
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
        }).ToListAsync();

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
        return await _repository.Query().Where(t=>t.UserId==userId).Select(t => new TransactionDto
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
        }).ToListAsync();
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

    private async Task EnsureCategoryOwnedAsync(int? categoryId, int userId, TransactionType transactionType)
    {
        if (!categoryId.HasValue) return;
        var category = await _context.Categories.FirstOrDefaultAsync(c => c.Id == categoryId.Value && c.UserId == userId);
        if (category == null)
            throw new BadRequestException("Geçersiz kategori!");
        if (category.Type != transactionType)
            throw new BadRequestException("Kategori tipi işlem tipiyle uyuşmuyor!");
    }

    public async Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto)
    {
        // Token'dan UserId al
        var userId = int.Parse(_httpContextAccessor.HttpContext!.User
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        await EnsureCategoryOwnedAsync(dto.CategoryId, userId, dto.Type);

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

        await EnsureCategoryOwnedAsync(dto.CategoryId, userId, dto.Type);

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
        var monthly = _repository.Query().Where(t=>t.UserId==userId && t.TransactionDate.Month==month && t.TransactionDate.Year==year);

        var totalIncome=await monthly.Where(t=>t.Type==TransactionType.Income).SumAsync(t=>t.Amount);

        var totalExpense=await monthly.Where(t=>t.Type==TransactionType.Expense).SumAsync(t=>t.Amount);

        return new MonthlySummaryDto{
            TotalIncome=totalIncome,
            TotalExpense=totalExpense,
            Balance=totalIncome-totalExpense,
            TransactionCount=await monthly.CountAsync(),
            Month=month,
            Year=year
        };
    }
}