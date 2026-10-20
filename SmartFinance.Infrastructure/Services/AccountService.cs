using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Account;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Accounts;
using SmartFinance.Infrastructure.Context;

namespace SmartFinance.Infrastructure.Services;

public class AccountService : IAccountService
{
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _user;
    private readonly IEntitlementService _entitlement;

    public AccountService(SmartFinanceDbContext context, ICurrentUserService user, IEntitlementService entitlement)
    {
        _context = context;
        _user = user;
        _entitlement = entitlement;
    }

    private async Task<Account> OwnedAsync(int id) =>
        await _context.Accounts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _user.UserId)
        ?? throw new NotFoundException("Hesap bulunamadı");

    private async Task EnsureLimitAsync() => await _entitlement.EnsureWithinFreeLimitAsync(
        await _context.Accounts.CountAsync(x => x.UserId == _user.UserId && !x.IsArchived), FreeTierLimits.Accounts,
        "Ücretsiz planda en fazla 2 hesap tanımlayabilirsiniz. Sınırsız hesap için Premium'a geçin.");

    private static void Validate(CreateAccountDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 60 || !Enum.IsDefined(dto.Type))
            throw new BadRequestException("Geçersiz hesap adı veya türü");
        if (dto.Type == AccountType.CreditCard)
        {
            if (!dto.CreditLimit.HasValue || dto.CreditLimit <= 0 || dto.StatementDay is null or < 1 or > 28
                || dto.DueDay is null or < 1 or > 28 || dto.OpeningBalance < 0)
                throw new BadRequestException("Kart limiti pozitif, kesim ve ödeme günleri 1–28 olmalıdır; açılış borcu negatif olamaz.");
        }
        else if (dto.CreditLimit.HasValue || dto.StatementDay.HasValue || dto.DueDay.HasValue)
            throw new BadRequestException("Kart alanları yalnız kredi kartında kullanılabilir.");
    }

    private static void Apply(Account account, CreateAccountDto dto)
    {
        account.Name = dto.Name.Trim();
        account.Type = dto.Type;
        account.OpeningBalance = dto.OpeningBalance ?? 0m;
        account.CreditLimit = dto.CreditLimit;
        account.StatementDay = dto.StatementDay;
        account.DueDay = dto.DueDay;
    }

    public async Task<IReadOnlyList<AccountDto>> GetAllAsync(bool includeArchived = false)
    {
        var accounts = await _context.Accounts.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && (includeArchived || !x.IsArchived))
            .OrderBy(x => x.IsArchived).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
        return await MapAsync(accounts);
    }

    // Bütün bakiye/borç hesapları yalnız bu metotta; liste için sabit sayıda sorgu.
    private async Task<IReadOnlyList<AccountDto>> MapAsync(IReadOnlyList<Account> accounts)
    {
        var ids = accounts.Select(x => x.Id).ToArray();
        var net = await _context.Transactions.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && x.AccountId.HasValue && ids.Contains(x.AccountId.Value))
            .GroupBy(x => x.AccountId!.Value)
            .Select(g => new { Id = g.Key, Amount = g.Sum(x => x.Type == TransactionType.Income ? x.Amount : -x.Amount) })
            .ToDictionaryAsync(x => x.Id, x => x.Amount);
        var incoming = await _context.Transfers.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && ids.Contains(x.ToAccountId)).GroupBy(x => x.ToAccountId)
            .Select(g => new { Id = g.Key, Amount = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Amount);
        var outgoing = await _context.Transfers.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && ids.Contains(x.FromAccountId)).GroupBy(x => x.FromAccountId)
            .Select(g => new { Id = g.Key, Amount = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Amount);
        return accounts.Select(a =>
        {
            var movement = net.GetValueOrDefault(a.Id) + incoming.GetValueOrDefault(a.Id) - outgoing.GetValueOrDefault(a.Id);
            var card = a.Type == AccountType.CreditCard;
            var debt = a.OpeningBalance - movement;
            return new AccountDto
            {
                Id = a.Id, Name = a.Name, Type = a.Type, OpeningBalance = a.OpeningBalance,
                CreditLimit = a.CreditLimit, StatementDay = a.StatementDay, DueDay = a.DueDay,
                IsArchived = a.IsArchived, SortOrder = a.SortOrder,
                Balance = card ? null : a.OpeningBalance + movement,
                Debt = card ? debt : null, AvailableLimit = card ? a.CreditLimit - debt : null
            };
        }).ToList();
    }

    public async Task<AccountDto> GetAsync(int id) => (await MapAsync(new[] { await OwnedAsync(id) }))[0];

    public async Task<AccountDto> CreateAsync(CreateAccountDto dto)
    {
        Validate(dto);
        await EnsureLimitAsync();
        var account = new Account { UserId = _user.UserId };
        Apply(account, dto);
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();
        return await GetAsync(account.Id);
    }

    public async Task<AccountDto> UpdateAsync(int id, CreateAccountDto dto)
    {
        var account = await OwnedAsync(id);
        if (account.Type != dto.Type) throw new BadRequestException("Hesap türü değiştirilemez.");
        Validate(dto);
        Apply(account, dto);
        account.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var account = await OwnedAsync(id);
        // Çöp kutusundaki işlem/transfer de FK ile bağlıdır; geri alma bağı korunur.
        var hasHistory = await _context.Transactions.IgnoreQueryFilters()
            .AnyAsync(x => x.UserId == _user.UserId && x.AccountId == id)
            || await _context.Transfers.IgnoreQueryFilters()
            .AnyAsync(x => x.UserId == _user.UserId && (x.FromAccountId == id || x.ToAccountId == id));
        if (hasHistory) account.IsArchived = true;
        else
        {
            // Hareketsiz hesabın 204 yolunda görünmeyen planların FK bağı da temizlenir.
            var plans = await _context.RecurringPayments.IgnoreQueryFilters()
                .Where(x => x.AccountId == id).ToListAsync();
            foreach (var plan in plans) plan.AccountId = null;
            account.IsDeleted = true;
        }
        account.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return hasHistory;
    }

    public async Task<AccountDto> UnarchiveAsync(int id)
    {
        var account = await OwnedAsync(id);
        if (account.IsArchived)
        {
            await EnsureLimitAsync();
            account.IsArchived = false;
            account.UpdatedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
        return await GetAsync(id);
    }

    private static TransferDto MapTransfer(Transfer x) => new()
    {
        Id = x.Id, FromAccountId = x.FromAccountId, ToAccountId = x.ToAccountId,
        Amount = x.Amount, TransferDate = DateTime.SpecifyKind(x.TransferDate, DateTimeKind.Utc), Description = x.Description
    };

    public async Task<TransferDto> CreateTransferAsync(CreateTransferDto dto)
    {
        // Sahiplik önce: başka kullanıcının kimliği her durumda 404.
        var from = await OwnedAsync(dto.FromAccountId);
        var to = await OwnedAsync(dto.ToAccountId);
        if (from.Id == to.Id || dto.Amount <= 0 || from.IsArchived || to.IsArchived
            || dto.Description?.Length > 200 || dto.TransferDate == default)
            throw new BadRequestException("Geçersiz transfer: farklı ve aktif hesaplar, pozitif tutar ve tarih gereklidir.");
        var transfer = new Transfer
        {
            UserId = _user.UserId, FromAccountId = from.Id, ToAccountId = to.Id,
            Amount = dto.Amount, Description = dto.Description,
            TransferDate = dto.TransferDate.Kind == DateTimeKind.Local ? dto.TransferDate.ToUniversalTime()
                : DateTime.SpecifyKind(dto.TransferDate, DateTimeKind.Utc)
        };
        _context.Transfers.Add(transfer);
        await _context.SaveChangesAsync();
        return MapTransfer(transfer);
    }

    public async Task<TransfersDto> GetTransfersAsync(int id, int page = 1, int pageSize = 50)
    {
        await OwnedAsync(id);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            throw new BadRequestException("Geçersiz sayfalama.");
        var query = _context.Transfers.AsNoTracking().Where(x => x.UserId == _user.UserId
            && (x.FromAccountId == id || x.ToAccountId == id));
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.TransferDate).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new TransfersDto { Items = items.Select(MapTransfer).ToList(), TotalCount = total };
    }

    public async Task DeleteTransferAsync(int id)
    {
        var transfer = await _context.Transfers.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _user.UserId)
            ?? throw new NotFoundException("Transfer bulunamadı");
        transfer.IsDeleted = true;
        transfer.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<StatementsDto> GetStatementsAsync(int id, int count = 3)
    {
        var account = await OwnedAsync(id);
        if (account.Type != AccountType.CreditCard || count is < 1 or > 12)
            throw new BadRequestException("Kredi kartı hesabı ve 1–12 ekstre istenmelidir.");
        var transactions = await _context.Transactions.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && x.AccountId == id)
            .Select(x => new { x.TransactionDate, x.Amount, x.Type }).ToListAsync();
        var payments = await _context.Transfers.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && x.ToAccountId == id)
            .Select(x => new { x.TransferDate, x.Amount }).ToListAsync();
        var statements = CardCycle.Calculate(account.StatementDay!.Value, account.DueDay!.Value, account.OpeningBalance,
            transactions.Select(x => new CardCycle.Entry(x.TransactionDate, x.Type == TransactionType.Expense ? x.Amount : -x.Amount)),
            payments.Select(x => new CardCycle.Entry(x.TransferDate, x.Amount)), DateTime.UtcNow, count);
        var createdDay = CardCycle.IstanbulDay(account.CreatedDate);
        // FIFO tamamlandıktan sonra yalnız görünümü süz; count penceresini eski dönemlerle doldurma.
        statements.Past = statements.Past.Where(x => x.Amount != 0 || x.Paid != 0
            || DateOnly.ParseExact(x.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture) >= createdDay).ToList();
        return statements;
    }
}
