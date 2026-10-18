using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class TransactionService : ITransactionService
{
    private readonly IGenericRepository<Transaction> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly INotificationService _notificationService;

    public TransactionService(IGenericRepository<Transaction> repository, SmartFinanceDbContext context,
        ICurrentUserService currentUserService, INotificationService notificationService)
    {
        _repository = repository;
        _context = context;
        _currentUserService = currentUserService;
        _notificationService = notificationService;
    }
    public async Task<object> GetFilteredTransactionsAsync(TransactionFilterDto filter)
    {
        var userId= _currentUserService.UserId;
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

        // SIRALAMA ZORUNLU: OrderBy olmadan SQL Server satirlari herhangi bir
        // sirada dondurebilir (pratikte kumelenmis indeks sirasi, yani ekleme
        // sirasi). Bu yuzden "Son Islemler" en yeni degil EN ESKI kayitlari
        // gosteriyordu ve yeni eklenen islemler listenin en sonuna dusuyordu.
        //
        // Ikincil siralama Id: TransactionDate saat bilgisi tasimiyor, ayni
        // gune ait cok sayida kayit esitleniyor. Esitlik deterministik
        // bozulmazsa OFFSET/FETCH sayfalari kararsiz kalir — ayni kayit iki
        // sayfada birden cikabilir veya hic cikmayabilir.
        query = query
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id);

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
        var userId=_currentUserService.UserId;
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
        var userId=_currentUserService.UserId;
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
        var userId = _currentUserService.UserId;

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

        if (transaction.Type == TransactionType.Expense)
            await _notificationService.EvaluateBudgetForTransactionAsync(userId, transaction.CategoryId, transaction.TransactionDate);

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
        var userId=_currentUserService.UserId;
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

        if (transaction.Type == TransactionType.Expense)
            await _notificationService.EvaluateBudgetForTransactionAsync(userId, transaction.CategoryId, transaction.TransactionDate);
    }

    public async Task DeleteTransactionAsync(int id)
    {
        var userId=_currentUserService.UserId;
        var transaction = await _repository.GetByIdAsync(id);
        if(transaction == null || transaction.UserId!=userId)
            throw new NotFoundException("İşlem bulunamadı!");
        transaction.IsDeleted = true;
        transaction.UpdatedDate = DateTime.UtcNow;
        _repository.Update(transaction);
        await _context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<DeletedTransactionDto>> GetDeletedTransactionsAsync()
    {
        var userId = _currentUserService.UserId;
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-30);
        // IgnoreQueryFilters kullanilirken kullanici ve silinme kosullari zorunlu.
        var items = await _context.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.UserId == userId && t.IsDeleted
                && t.UpdatedDate.HasValue && t.UpdatedDate.Value >= cutoff && t.UpdatedDate.Value <= now)
            .OrderByDescending(t => t.UpdatedDate)
            .ThenByDescending(t => t.Id)
            .Take(200)
            .Select(t => new DeletedTransactionDto
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
                DeletedAt = t.UpdatedDate!.Value
            }).ToListAsync();

        // SQL datetime Kind saklamaz; kaydedilen UTC degerini saat dilimi donusumu yapmadan isaretle.
        foreach (var item in items)
            item.DeletedAt = DateTime.SpecifyKind(item.DeletedAt, DateTimeKind.Utc);

        return items;
    }

    public async Task<TransactionDto?> RestoreTransactionAsync(int id)
    {
        var userId = _currentUserService.UserId;
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-30);
        var transaction = await _context.Transactions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId && t.IsDeleted
                && t.UpdatedDate.HasValue && t.UpdatedDate.Value >= cutoff && t.UpdatedDate.Value <= now);
        if (transaction == null) return null;

        // Kategori filtresini kaldirma: silinmis veya kayip kategori geri getirilmez.
        if (transaction.CategoryId.HasValue && !await _context.Categories
            .AnyAsync(c => c.Id == transaction.CategoryId.Value && c.UserId == userId))
            transaction.CategoryId = null;

        transaction.IsDeleted = false;
        transaction.UpdatedDate = now;
        await _context.SaveChangesAsync();

        // Geri alma yeni gider degildir; butce/bildirim degerlendirmesi yapilmaz.
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
            CreatedDate = transaction.CreatedDate
        };
    }

    // Aylik ozetin mevcut yil/ay mantigi iki uc icin de aynidir.
    private IQueryable<Transaction> MonthlyTransactions(int month, int year)
    {
        var userId = _currentUserService.UserId;
        return _repository.Query().Where(t => t.UserId == userId
            && t.TransactionDate.Month == month && t.TransactionDate.Year == year);
    }

    public async Task<IReadOnlyList<CategorySummaryDto>> GetCategorySummaryAsync(int month, int year, int type = 2)
    {
        if (month < 1 || month > 12 || (type != 1 && type != 2))
            throw new BadRequestException("Geçersiz ay veya işlem tipi!");

        return await MonthlyTransactions(month, year)
            .Where(t => t.Type == (TransactionType)type)
            .GroupBy(t => new { t.CategoryId, CategoryName = t.Category == null ? "Kategorisiz" : t.Category.Name })
            .Select(g => new CategorySummaryDto
            {
                CategoryId = g.Key.CategoryId,
                CategoryName = g.Key.CategoryName,
                Total = g.Sum(t => t.Amount),
                Count = g.Count()
            })
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.CategoryId)
            .ToListAsync();
    }

    public async Task<MonthlySummaryDto> GetMonthlySummaryAsync(int month, int year)
    {
        var monthly = MonthlyTransactions(month, year);

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
