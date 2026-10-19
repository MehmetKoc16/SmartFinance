using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.RecurringPayment;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Recurring;

namespace SmartFinance.Infrastructure.Services;

public class RecurringPaymentService : IRecurringPaymentService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _user;
    private readonly IEntitlementService _entitlement;
    private readonly ITransactionService _transactions;
    private readonly TimeProvider _clock;

    public RecurringPaymentService(SmartFinanceDbContext context, ICurrentUserService user,
        IEntitlementService entitlement, ITransactionService transactions, TimeProvider? clock = null)
    {
        _context = context;
        _user = user;
        _entitlement = entitlement;
        _transactions = transactions;
        _clock = clock ?? TimeProvider.System;
    }

    private DateOnly Today => RecurrenceSchedule.TodayTr(_clock.GetUtcNow().UtcDateTime);

    private async Task<RecurringPayment> OwnedAsync(int id) =>
        await _context.RecurringPayments.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _user.UserId)
        ?? throw new NotFoundException("Planlı ödeme bulunamadı.");

    private async Task ValidateLinksAsync(int? categoryId, TransactionType type, int? accountId)
    {
        if (categoryId.HasValue)
        {
            var category = await _context.Categories.FirstOrDefaultAsync(x => x.Id == categoryId && x.UserId == _user.UserId);
            if (category == null) throw new BadRequestException("Geçersiz kategori!");
            if (category.Type != type) throw new BadRequestException("Kategori tipi işlem tipiyle uyuşmuyor!");
        }
        if (accountId.HasValue && !await _context.Accounts.AnyAsync(x => x.Id == accountId
            && x.UserId == _user.UserId && !x.IsArchived))
            throw new BadRequestException("Hesap bulunamadı");
    }

    private async Task ValidateAsync(CreateRecurringPaymentDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 100)
            throw new BadRequestException("Planlı ödeme adı zorunludur ve en fazla 100 karakter olabilir.");
        ValidateAmount(dto.Amount);
        if (!Enum.IsDefined(dto.Type) || !Enum.IsDefined(dto.Frequency))
            throw new BadRequestException("Geçersiz ödeme türü veya tekrar sıklığı.");
        if (dto.RemindDaysBefore is < 0 or > 7)
            throw new BadRequestException("Hatırlatma günü 0–7 arasında olmalıdır.");
        if (dto.StartDate == default)
            throw new BadRequestException("Başlangıç tarihi zorunludur.");
        if (dto.EndDate < dto.StartDate)
            throw new BadRequestException("Bitiş tarihi başlangıç tarihinden önce olamaz.");
        await ValidateLinksAsync(dto.CategoryId, dto.Type, dto.AccountId);
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            throw new BadRequestException("Tutar pozitif, en fazla iki ondalık basamaklı ve decimal(18,2) sınırında olmalıdır.");
    }

    private static void Apply(RecurringPayment p, CreateRecurringPaymentDto dto)
    {
        p.Name = dto.Name.Trim(); p.Amount = dto.Amount; p.Type = dto.Type;
        p.CategoryId = dto.CategoryId; p.AccountId = dto.AccountId; p.Frequency = dto.Frequency;
        p.StartDate = dto.StartDate; p.EndDate = dto.EndDate; p.RemindDaysBefore = dto.RemindDaysBefore;
    }

    private void ResetSchedule(RecurringPayment p)
    {
        p.AnchorDay = p.StartDate.Day;
        p.NextDueDate = p.StartDate;
        var today = Today;
        while (p.NextDueDate < today && RecurrenceSchedule.CanAdvance(p.NextDueDate, p.Frequency))
            p.NextDueDate = RecurrenceSchedule.Next(p.NextDueDate, p.Frequency, p.AnchorDay);
        p.IsActive = !p.EndDate.HasValue || p.NextDueDate <= p.EndDate.Value;
    }

    private static string Status(DateOnly due, DateOnly today, bool active = true) =>
        !active ? "ended" : due < today ? "overdue" : due == today ? "today" : "upcoming";

    private static RecurringPaymentDto Map(RecurringPayment p, DateOnly today) => new()
    {
        Id = p.Id, Name = p.Name, Amount = p.Amount, Type = p.Type, CategoryId = p.CategoryId,
        AccountId = p.AccountId, Frequency = p.Frequency, StartDate = p.StartDate, AnchorDay = p.AnchorDay,
        NextDueDate = p.NextDueDate, EndDate = p.EndDate, RemindDaysBefore = p.RemindDaysBefore,
        IsActive = p.IsActive, DaysUntil = p.NextDueDate.DayNumber - today.DayNumber,
        Status = Status(p.NextDueDate, today, p.IsActive)
    };

    public async Task<IReadOnlyList<RecurringPaymentDto>> ListAsync()
    {
        var today = Today;
        var items = await _context.RecurringPayments.AsNoTracking().Where(x => x.UserId == _user.UserId)
            .OrderBy(x => x.NextDueDate).ThenBy(x => x.Id).ToListAsync();
        return items.Select(x => Map(x, today)).ToList();
    }

    public async Task<RecurringPaymentDto> CreateAsync(CreateRecurringPaymentDto dto)
    {
        await ValidateAsync(dto);
        await _entitlement.EnsureWithinFreeLimitAsync(
            await _context.RecurringPayments.CountAsync(x => x.UserId == _user.UserId && x.IsActive),
            FreeTierLimits.RecurringPayments,
            "Ücretsiz planda en fazla 3 planlı ödeme tanımlayabilirsiniz. Sınırsız planlı ödeme için Premium'a geçin.");
        var p = new RecurringPayment { UserId = _user.UserId };
        Apply(p, dto); ResetSchedule(p);
        _context.RecurringPayments.Add(p);
        await _context.SaveChangesAsync();
        return Map(p, Today);
    }

    public async Task<RecurringPaymentDto> UpdateAsync(int id, CreateRecurringPaymentDto dto)
    {
        var p = await OwnedAsync(id);
        await ValidateAsync(dto);
        var reset = p.StartDate != dto.StartDate || p.Frequency != dto.Frequency;
        Apply(p, dto);
        if (reset) ResetSchedule(p);
        else if (p.EndDate.HasValue && p.NextDueDate > p.EndDate.Value) p.IsActive = false;
        p.UpdatedDate = _clock.GetUtcNow().UtcDateTime;
        await SaveAsync();
        return Map(p, Today);
    }

    public async Task DeleteAsync(int id)
    {
        var p = await OwnedAsync(id);
        p.IsDeleted = true; p.UpdatedDate = _clock.GetUtcNow().UtcDateTime;
        await SaveAsync();
    }

    private static void EnsureDue(RecurringPayment p, DateOnly? dueDate)
    {
        if (!dueDate.HasValue || dueDate.Value == default) throw new BadRequestException("Vade tarihi zorunludur.");
        // Önce vade: son vade planı bitirmiş olsa bile aynı dokunuşun tekrarı 409'dur.
        if (dueDate.Value != p.NextDueDate) throw new ConflictException("Bu vade zaten işlendi.");
        if (!p.IsActive) throw new BadRequestException("Pasif planlı ödeme işlenemez.");
    }

    private void Advance(RecurringPayment p)
    {
        if (!RecurrenceSchedule.CanAdvance(p.NextDueDate, p.Frequency)) p.IsActive = false;
        else
        {
            p.NextDueDate = RecurrenceSchedule.Next(p.NextDueDate, p.Frequency, p.AnchorDay);
            if (p.EndDate.HasValue && p.NextDueDate > p.EndDate.Value) p.IsActive = false;
        }
        p.UpdatedDate = _clock.GetUtcNow().UtcDateTime;
    }

    private async Task SaveAsync()
    {
        try { await _context.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Bu vade zaten işlendi."); }
    }

    public async Task<PayRecurringPaymentResultDto> PayAsync(int id, PayRecurringPaymentDto dto)
    {
        var p = await OwnedAsync(id);
        EnsureDue(p, dto.DueDate);
        var amount = dto.Amount ?? p.Amount;
        ValidateAmount(amount);
        if (dto.Description?.Length > 500) throw new BadRequestException("Açıklama en fazla 500 karakter olabilir!");
        if (dto.TransactionDate.HasValue && (dto.TransactionDate.Value == default || dto.TransactionDate.Value.Kind != DateTimeKind.Utc))
            throw new BadRequestException("İşlem tarihi UTC olmalıdır.");
        var accountId = dto.AccountId ?? p.AccountId;
        await ValidateLinksAsync(p.CategoryId, p.Type, accountId);
        Advance(p);
        try
        {
            // Aynı scoped DbContext: TransactionService'in tek SaveChanges'i işlem + vade
            // değişikliğini atomik yazar. Ardından mevcut bütçe/push yan etkileri çalışır.
            // NextDueDate concurrency token'ı yarışta kaybeden isteği geri alır.
            var transaction = await _transactions.CreateTransactionAsync(new CreateTransactionDto
            {
                Amount = amount, Type = p.Type, CategoryId = p.CategoryId, AccountId = accountId,
                Description = dto.Description ?? p.Name, TransactionDate = dto.TransactionDate ?? _clock.GetUtcNow().UtcDateTime
            });
            return new PayRecurringPaymentResultDto { Transaction = transaction, RecurringPayment = Map(p, Today) };
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Bu vade zaten işlendi."); }
    }

    public async Task<RecurringPaymentDto> SkipAsync(int id, ProcessRecurringPaymentDto dto)
    {
        var p = await OwnedAsync(id);
        EnsureDue(p, dto.DueDate); Advance(p);
        await SaveAsync();
        return Map(p, Today);
    }

    public async Task<UpcomingRecurringPaymentsDto> UpcomingAsync(int days = 30)
    {
        if (days is < 1 or > 90) throw new BadRequestException("Gün sayısı 1–90 arasında olmalıdır.");
        var today = Today; var through = today.AddDays(days);
        var plans = await _context.RecurringPayments.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && x.IsActive && x.NextDueDate <= through).ToListAsync();
        var items = new List<UpcomingRecurringPaymentDto>();
        foreach (var p in plans)
        {
            if (p.NextDueDate < today && (!p.EndDate.HasValue || p.NextDueDate <= p.EndDate.Value))
                Add(p, p.NextDueDate);
            foreach (var due in RecurrenceSchedule.Occurrences(p.NextDueDate, p.Frequency, p.AnchorDay, today, through, p.EndDate))
                Add(p, due);
        }
        return new UpcomingRecurringPaymentsDto
        {
            Items = items.OrderBy(x => x.DueDate).ThenBy(x => x.RecurringPaymentId).ToList(),
            ExpenseTotal = items.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount),
            IncomeTotal = items.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount)
        };
        void Add(RecurringPayment p, DateOnly due) => items.Add(new UpcomingRecurringPaymentDto
        {
            RecurringPaymentId = p.Id, Name = p.Name, Amount = p.Amount, Type = p.Type,
            CategoryId = p.CategoryId, AccountId = p.AccountId, DueDate = due, Status = Status(due, today)
        });
    }
}
