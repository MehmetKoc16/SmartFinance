using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.Application.DTOs.Account;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;
using System.Text.Json;

namespace SmartFinance.Tests;

public class AccountServiceTests
{
    private sealed class Ortam : IDisposable
    {
        public SmartFinanceDbContext Db { get; } = new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        public ICurrentUserService User { get; }
        public AccountService Service { get; }
        public TransactionService Transactions { get; }
        public Ortam(bool premium = false)
        {
            var user = new Mock<ICurrentUserService>();
            user.SetupGet(x => x.UserId).Returns(1);
            User = user.Object;
            var entitlement = new EntitlementService(Db, User, new ConfigurationBuilder().Build());
            if (premium) Db.Subscriptions.Add(new Subscription { UserId = 1, ProductId = "test", PurchaseToken = Guid.NewGuid().ToString(), StartsAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(1) });
            Db.SaveChanges();
            Service = new AccountService(Db, User, entitlement);
            Transactions = new TransactionService(new GenericRepository<Transaction>(Db), Db, User, new NotificationService(Db, User));
        }
        public void Dispose() => Db.Dispose();
        public async Task<AccountDto> Hesap(AccountType type = AccountType.Cash, decimal opening = 0) =>
            await Service.CreateAsync(new CreateAccountDto { Name = "Deneme", Type = type, OpeningBalance = opening,
                CreditLimit = type == AccountType.CreditCard ? 5000 : null,
                StatementDay = type == AccountType.CreditCard ? 20 : null, DueDay = type == AccountType.CreditCard ? 25 : null });
        public async Task Islem(int id, decimal amount, TransactionType type) => await Transactions.CreateTransactionAsync(new CreateTransactionDto
            { AccountId = id, Amount = amount, Type = type, TransactionDate = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc) });
        public Task<TransferDto> Transfer(int from, int to, decimal amount) => Service.CreateTransferAsync(new CreateTransferDto
            { FromAccountId = from, ToAccountId = to, Amount = amount, TransferDate = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc) });
    }

    // NEDEN: Dört hareket türü tek bakiye formülünde doğru işaretle birleşmeli.
    [Fact]
    public async Task NakitBakiye_AcilisGelirGiderTransfer()
    {
        using var o = new Ortam();
        var a = await o.Hesap(opening: 100); var b = await o.Hesap(AccountType.Bank);
        await o.Islem(a.Id, 250, TransactionType.Income); await o.Islem(a.Id, 40, TransactionType.Expense);
        await o.Transfer(b.Id, a.Id, 30); await o.Transfer(a.Id, b.Id, 20);
        Assert.Equal(320m, (await o.Service.GetAsync(a.Id)).Balance); // 100+250-40+30-20=320
    }

    // NEDEN: Kartta ödeme borcu azaltır, avans artırır; limit negatif olabilir.
    [Fact]
    public async Task KartBorcu_HarcamaIadeOdemeAvans()
    {
        using var o = new Ortam();
        var a = await o.Hesap(AccountType.CreditCard, 100); var b = await o.Hesap();
        await o.Islem(a.Id, 6000, TransactionType.Expense); await o.Islem(a.Id, 200, TransactionType.Income);
        await o.Transfer(b.Id, a.Id, 500); await o.Transfer(a.Id, b.Id, 50);
        var result = await o.Service.GetAsync(a.Id);
        Assert.Equal(5450m, result.Debt); // 100+6000-200-500+50=5450
        Assert.Equal(-450m, result.AvailableLimit);
        Assert.Null(result.Balance);
    }

    // NEDEN: Kart borcu ödemesi gerçek harcamayı ikinci kez gider yapmamalı.
    [Fact]
    public async Task KartOdemesi_AylikGidereCiftSayilmaz()
    {
        using var o = new Ortam(); var card = await o.Hesap(AccountType.CreditCard); var bank = await o.Hesap(AccountType.Bank);
        await o.Islem(card.Id, 1000, TransactionType.Expense); await o.Transfer(bank.Id, card.Id, 1000);
        var summary = await o.Transactions.GetMonthlySummaryAsync(10, 2026);
        Assert.Equal(1000m, summary.TotalExpense); Assert.Equal(1, summary.TransactionCount);
        Assert.Equal(1000m, Assert.Single(await o.Transactions.GetCategorySummaryAsync(10, 2026)).Total);
    }

    // NEDEN: Kimlikler tahmin edilse bile başka kullanıcının hesabı açılmamalı.
    [Fact]
    public async Task BaskasininHesabi_404()
    {
        using var o = new Ortam();
        var other = new Account { UserId = 2, Name = "Başka", Type = AccountType.Bank }; o.Db.Accounts.Add(other); await o.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.GetAsync(other.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.UpdateAsync(other.Id, new CreateAccountDto { Name = "Yeni", Type = AccountType.Bank }));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.UnarchiveAsync(other.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.DeleteAsync(other.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.GetTransfersAsync(other.Id, 1, 50));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.GetStatementsAsync(other.Id, 3));
        var mine = await o.Hesap();
        await Assert.ThrowsAsync<NotFoundException>(() => o.Transfer(mine.Id, other.Id, 10));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Transactions.GetFilteredTransactionsAsync(new TransactionFilterDto { AccountId = other.Id }));
        var foreignTransfer = new Transfer { UserId = 2, FromAccountId = other.Id, ToAccountId = 999, Amount = 10 };
        o.Db.Transfers.Add(foreignTransfer); await o.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.DeleteTransferAsync(foreignTransfer.Id));
    }

    // NEDEN: Ücretsiz sınır servis katmanında mevcut 402 hatasını kullanmalı.
    [Fact]
    public async Task UcretsizPlan_UcuncuHesap402VeyaMevcutPremiumHatasi()
    {
        using var o = new Ortam(); await o.Hesap(); await o.Hesap();
        var ex = await Assert.ThrowsAsync<PremiumRequiredException>(() => o.Hesap());
        Assert.Equal("Ücretsiz planda en fazla 2 hesap tanımlayabilirsiniz. Sınırsız hesap için Premium'a geçin.", ex.Message);
    }

    // NEDEN: Premium kullanıcının hesap adedi sınırlandırılmamalı.
    [Fact]
    public async Task Premium_SinirsizHesap()
    {
        using var o = new Ortam(true); for (var i = 0; i < 5; i++) await o.Hesap();
        Assert.Equal(5, (await o.Service.GetAllAsync()).Count);
    }

    // NEDEN: Silinen işlemin geri alınabilmesi için hesap bağlantısı korunmalı.
    [Fact]
    public async Task IsleminiVarsaSilmekArsivler_YoksaSiler()
    {
        using var o = new Ortam(); var a = await o.Hesap(); var b = await o.Hesap();
        await o.Islem(a.Id, 10, TransactionType.Expense);
        await o.Transactions.DeleteTransactionAsync(o.Db.Transactions.Single().Id);
        Assert.True(await o.Service.DeleteAsync(a.Id)); Assert.False(await o.Service.DeleteAsync(b.Id));
        Assert.True(o.Db.Accounts.Single().IsArchived);
        Assert.True(o.Db.Accounts.IgnoreQueryFilters().Single(x => x.Id == b.Id && x.UserId == 1).IsDeleted);
    }

    // NEDEN: Tür değişikliği geçmiş bakiyenin işaretini değiştirmemeli.
    [Fact]
    public async Task TurDegistirilemez400()
    {
        using var o = new Ortam(); var a = await o.Hesap();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.UpdateAsync(a.Id, new CreateAccountDto { Name = "Yeni", Type = AccountType.Bank }));
    }

    // NEDEN: Kart alanları nakit ve banka hesaplarına sızmamalı.
    [Fact]
    public async Task KartAlanlari_KartDisindaGelirse400()
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.CreateAsync(new CreateAccountDto { Name = "Nakit", Type = AccountType.Cash, CreditLimit = 100 }));
    }

    // NEDEN: Geçersiz transferler bakiye üretmemeli.
    [Fact]
    public async Task Transfer_AyniHesap400_SifirTutar400_ArsivliHesap400()
    {
        using var o = new Ortam(); var a = await o.Hesap(); var b = await o.Hesap();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Transfer(a.Id, a.Id, 10));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Transfer(a.Id, b.Id, 0));
        o.Db.Accounts.Single(x => x.Id == b.Id).IsArchived = true; await o.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Transfer(a.Id, b.Id, 10));
    }

    // NEDEN: Hesap filtresi diğer hesapları ve hesapsız işlemleri dışarıda tutmalı.
    [Fact]
    public async Task IslemFiltresi_AccountIdIleSuzer()
    {
        using var o = new Ortam(); var a = await o.Hesap(); var b = await o.Hesap();
        await o.Islem(a.Id, 10, TransactionType.Expense); await o.Islem(b.Id, 20, TransactionType.Expense);
        var json = JsonSerializer.SerializeToElement(await o.Transactions.GetFilteredTransactionsAsync(new TransactionFilterDto { AccountId = a.Id }));
        Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(a.Id, json.GetProperty("items")[0].GetProperty("AccountId").GetInt32());
    }

    // NEDEN: Arşivlenmiş hesap sayılmamalı; yeniden açmak limiti atlamamalı.
    [Fact]
    public async Task ArsivdenCikarma_UcretsizSiniriKontrolEder()
    {
        using var o = new Ortam(); var a = await o.Hesap(); var b = await o.Hesap();
        await o.Transfer(a.Id, b.Id, 10);
        Assert.True(await o.Service.DeleteAsync(a.Id));
        await o.Hesap();
        Assert.Equal(2, (await o.Service.GetAllAsync()).Count);
        Assert.Equal(3, (await o.Service.GetAllAsync(true)).Count);
        await Assert.ThrowsAsync<PremiumRequiredException>(() => o.Service.UnarchiveAsync(a.Id));
        await o.Service.DeleteAsync(b.Id);
        Assert.False((await o.Service.UnarchiveAsync(a.Id)).IsArchived);
    }

    // NEDEN: Hesap doğrulaması hem oluşturma hem güncellemede aynı olmalı.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Islem_GecersizHesap400_OlusturmaVeGuncelleme(int scenario)
    {
        using var o = new Ortam();
        var account = new Account { UserId = scenario == 0 ? 2 : 1, Name = "Geçersiz", Type = AccountType.Cash,
            IsArchived = scenario == 1, IsDeleted = scenario == 2 };
        o.Db.Accounts.Add(account); await o.Db.SaveChangesAsync();
        var dto = new CreateTransactionDto { Amount = 10, Type = TransactionType.Expense, TransactionDate = DateTime.UtcNow };
        var transaction = await o.Transactions.CreateTransactionAsync(dto);
        Assert.Null(transaction.AccountId);
        dto.AccountId = account.Id;
        var ex = await Assert.ThrowsAsync<BadRequestException>(() => o.Transactions.CreateTransactionAsync(dto));
        Assert.Equal("Hesap bulunamadı", ex.Message);
        await Assert.ThrowsAsync<BadRequestException>(() => o.Transactions.UpdateTransactionAsync(transaction.Id, dto));
        Assert.Null(o.Db.Transactions.Single().AccountId);
    }

    // NEDEN: Çöp kutusu ve geri alma, hesap bilgisini kaybetmemeli.
    [Fact]
    public async Task Islem_HesapBilgisiGuncellemeCopKutusuVeGeriAlmadaKorunur()
    {
        using var o = new Ortam(); var a = await o.Hesap(); var b = await o.Hesap();
        await o.Islem(a.Id, 10, TransactionType.Expense);
        var id = o.Db.Transactions.Single().Id;
        await o.Transactions.UpdateTransactionAsync(id, new UpdateTransactionDto { AccountId = b.Id, Amount = 20,
            Type = TransactionType.Expense, TransactionDate = DateTime.UtcNow });
        Assert.Equal(b.Id, (await o.Transactions.GetTransactionByIdAsync(id))!.AccountId);
        Assert.Equal(b.Id, Assert.Single(await o.Transactions.GetAllTransactionsAsync()).AccountId);
        await o.Transactions.DeleteTransactionAsync(id);
        Assert.Equal(b.Id, Assert.Single(await o.Transactions.GetDeletedTransactionsAsync()).AccountId);
        Assert.Equal(b.Id, (await o.Transactions.RestoreTransactionAsync(id))!.AccountId);
    }

    // NEDEN: Transfer silmek bakiyeyi geri almalı, sayfalama toplamı soft-delete'i dışlamalı.
    [Fact]
    public async Task Transfer_SoftDeleteBakiyeyiGeriAlir_SayfalamaTariheGore()
    {
        using var o = new Ortam(); var a = await o.Hesap(opening: 100); var b = await o.Hesap();
        var first = await o.Transfer(a.Id, b.Id, 10);
        var second = await o.Service.CreateTransferAsync(new CreateTransferDto { FromAccountId = a.Id, ToAccountId = b.Id,
            Amount = 20, TransferDate = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc) });
        var page = await o.Service.GetTransfersAsync(a.Id, 1, 1);
        Assert.Equal(2, page.TotalCount); Assert.Equal(second.Id, Assert.Single(page.Items).Id);
        Assert.Equal(70m, (await o.Service.GetAsync(a.Id)).Balance);
        await o.Service.DeleteTransferAsync(first.Id);
        Assert.Equal(80m, (await o.Service.GetAsync(a.Id)).Balance);
        Assert.Equal(1, (await o.Service.GetTransfersAsync(b.Id)).TotalCount);
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.GetTransfersAsync(a.Id, 1, 101));
    }

    // NEDEN: Kart için zorunlu alanlar ve enum sınırı servis seviyesinde uygulanmalı.
    [Theory]
    [InlineData(3, 0, 20, 25)]
    [InlineData(3, 100, 0, 25)]
    [InlineData(3, 100, 29, 25)]
    [InlineData(3, 100, 20, 29)]
    [InlineData(4, 100, 20, 25)]
    public async Task Hesap_GecersizKartAlanlari400(int type, int limit, int statementDay, int dueDay)
    {
        using var o = new Ortam();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.CreateAsync(new CreateAccountDto { Name = "Deneme",
            Type = (AccountType)type, CreditLimit = limit, StatementDay = statementDay, DueDay = dueDay }));
    }

    // NEDEN: Ekstre yalnız kartta ve sınırlı sayıda istenebilmeli.
    [Fact]
    public async Task Ekstre_KartOlmayanHesapVeGecersizSayida400()
    {
        using var o = new Ortam(); var cash = await o.Hesap(); var card = await o.Hesap(AccountType.CreditCard);
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.GetStatementsAsync(cash.Id, 3));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.GetStatementsAsync(card.Id, 0));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.GetStatementsAsync(card.Id, 13));
        Assert.Equal(3, (await o.Service.GetStatementsAsync(card.Id, 3)).Past.Count);
    }

    // NEDEN: Opsiyonel açılış bakiyesi null gönderildiğinde sıfır olmalı.
    [Fact]
    public async Task AcilisBakiyesi_VerilmezseSifir()
    {
        using var o = new Ortam();
        var result = await o.Service.CreateAsync(new CreateAccountDto { Name = "Nakit", Type = AccountType.Cash, OpeningBalance = null });
        Assert.Equal(0m, result.OpeningBalance); Assert.Equal(0m, result.Balance);
    }

    // NEDEN: Kalıcı kullanıcı silme soft-delete kayıtlarını da kaldırmalı, diğer kullanıcıyı korumalı.
    [Fact]
    public async Task HesapSilme_KullaniciSilinceHesapVeTransferlerDeSilinir()
    {
        using var o = new Ortam();
        o.Db.Users.Add(new User { Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = BCrypt.Net.BCrypt.HashPassword("Deneme123!") });
        var a = await o.Hesap(); var b = await o.Hesap(); await o.Islem(a.Id, 10, TransactionType.Expense);
        var transfer = await o.Transfer(a.Id, b.Id, 5);
        o.Db.Transfers.Single().IsDeleted = true; o.Db.Accounts.Single(x => x.Id == b.Id).IsDeleted = true;
        o.Db.Accounts.Add(new Account { UserId = 2, Name = "Korunacak", Type = AccountType.Cash });
        o.Db.Transfers.Add(new Transfer { UserId = 2, FromAccountId = 99, ToAccountId = 100, Amount = 7 });
        await o.Db.SaveChangesAsync();
        var auth = new AuthService(o.Db, new ConfigurationBuilder().Build(), o.User, new FakeEmailSender(), NullLogger<AuthService>.Instance);
        await auth.DeleteAccountAsync(new DeleteAccountDto { Password = "Deneme123!" });
        Assert.Empty(o.Db.Accounts.IgnoreQueryFilters().Where(x => x.UserId == 1));
        Assert.Empty(o.Db.Transfers.IgnoreQueryFilters().Where(x => x.UserId == 1));
        Assert.Single(o.Db.Accounts.Where(x => x.UserId == 2)); Assert.Single(o.Db.Transfers.Where(x => x.UserId == 2));
    }
}
