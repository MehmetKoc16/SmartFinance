using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Budget;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class FinanceRegressionTests
{
    private sealed class Ortam : IDisposable
    {
        public SmartFinanceDbContext Db { get; } = new(new DbContextOptionsBuilder<SmartFinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Mock<INotificationService> Notifications { get; } = new();
        public Mock<ILogger<TransactionService>> Logger { get; } = new();
        public TransactionService Transactions { get; }
        public BudgetService Budgets { get; }
        public Ortam()
        {
            var user = new Mock<ICurrentUserService>(); user.SetupGet(x => x.UserId).Returns(1);
            Transactions = new TransactionService(new GenericRepository<Transaction>(Db), Db, user.Object,
                Notifications.Object, Logger.Object);
            Budgets = new BudgetService(new GenericRepository<Budget>(Db), Db, user.Object,
                new EntitlementService(Db, user.Object, new ConfigurationBuilder().Build()));
        }
        public void Dispose() => Db.Dispose();
        public static CreateTransactionDto Islem(decimal amount = 23.125m) => new()
        {
            Amount = amount, Description = "Uydurma gider", Type = TransactionType.Expense,
            TransactionDate = new DateTime(2026, 9, 2, 12, 0, 0)
        };
        public static UpdateTransactionDto Guncelle(decimal amount = 34.125m) => new()
        {
            Amount = amount, Description = "Uydurma düzeltme", Type = TransactionType.Expense,
            TransactionDate = new DateTime(2026, 9, 2, 12, 0, 0)
        };
        public void BildirimHatasi(Exception ex) => Notifications.Setup(x => x.EvaluateBudgetForTransactionAsync(
            1, It.IsAny<int?>(), It.IsAny<DateTime>())).ThrowsAsync(ex);
    }

    [Fact]
    public async Task IslemEkleme_BildirimHatasi_IslemYineBasarili()
    {
        // NEDEN: Kaydedilmiş işlemin başarısız görünmesi istemcinin ikinci kez eklemesine yol açar.
        using var o = new Ortam();
        var exception = new InvalidOperationException("Uydurma kişisel içerik");
        o.BildirimHatasi(exception);
        var result = await o.Transactions.CreateTransactionAsync(Ortam.Islem());
        Assert.Equal(result.Id, (await o.Db.Transactions.AsNoTracking().SingleAsync()).Id);
        o.Notifications.Verify(x => x.EvaluateBudgetForTransactionAsync(1, null, result.TransactionDate), Times.Once);
        o.Logger.Verify(x => x.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("InvalidOperationException")
                && state.ToString()!.Contains("1") && !state.ToString()!.Contains("Uydurma kişisel içerik")),
            It.Is<Exception?>(ex => ex == null), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task IslemGuncelleme_BildirimHatasi_IslemYineBasarili()
    {
        // NEDEN: Güncellemenin kayıt sonrası bildirim hatası da başarılı kaydı gizlememeli.
        using var o = new Ortam(); var created = await o.Transactions.CreateTransactionAsync(Ortam.Islem());
        o.BildirimHatasi(new InvalidOperationException("Uydurma hata"));
        await o.Transactions.UpdateTransactionAsync(created.Id, Ortam.Guncelle());
        Assert.Equal(34.13m, (await o.Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Islem_BildirimIptali_Yutulmaz(bool update)
    {
        // NEDEN: İptal sinyali genel bildirim hatası gibi yutulmamalı.
        using var o = new Ortam();
        var created = update ? await o.Transactions.CreateTransactionAsync(Ortam.Islem()) : null;
        o.BildirimHatasi(new OperationCanceledException());
        if (update)
            await Assert.ThrowsAsync<OperationCanceledException>(() => o.Transactions.UpdateTransactionAsync(created!.Id, Ortam.Guncelle()));
        else
            await Assert.ThrowsAsync<OperationCanceledException>(() => o.Transactions.CreateTransactionAsync(Ortam.Islem()));
        Assert.Single(o.Db.Transactions);
    }

    [Fact]
    public async Task IslemEkleme_UcHaneliTutar_YanitVeKayitAyni()
    {
        // NEDEN: InMemory yuvarlamaz; yanıtın ve gerçek kaydın kuruşu servis tarafından eşitlenmeli.
        using var o = new Ortam(); var result = await o.Transactions.CreateTransactionAsync(Ortam.Islem());
        Assert.Equal(23.13m, result.Amount);
        Assert.Equal(result.Amount, (await o.Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task IslemGuncelleme_UcHaneliTutar_YanitVeKayitAyni()
    {
        // NEDEN: Eski istemci güncellemede de üç hane gönderebilir.
        using var o = new Ortam(); var created = await o.Transactions.CreateTransactionAsync(Ortam.Islem());
        await o.Transactions.UpdateTransactionAsync(created.Id, Ortam.Guncelle());
        var result = await o.Transactions.GetTransactionByIdAsync(created.Id);
        Assert.Equal(34.13m, result!.Amount);
        Assert.Equal(result.Amount, (await o.Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Islem_KolonaSigmayanTutar_Reddedilir(bool update)
    {
        // NEDEN: Taşma hem eklemede hem güncellemede kayıt öncesi yakalanmalı.
        using var o = new Ortam();
        var created = update ? await o.Transactions.CreateTransactionAsync(Ortam.Islem()) : null;
        var amount = MoneyRules.Max + 0.01m;
        var ex = update
            ? await Assert.ThrowsAsync<BadRequestException>(() => o.Transactions.UpdateTransactionAsync(created!.Id, Ortam.Guncelle(amount)))
            : await Assert.ThrowsAsync<BadRequestException>(() => o.Transactions.CreateTransactionAsync(Ortam.Islem(amount)));
        Assert.Equal("Tutar çok büyük.", ex.Message);
        if (update) Assert.Equal(23.13m, (await o.Db.Transactions.AsNoTracking().SingleAsync()).Amount);
        else Assert.Empty(o.Db.Transactions);
    }

    [Fact]
    public async Task Butce_UcHaneliLimit_Yuvarlanir()
    {
        // NEDEN: Bütçenin hem oluşturma hem güncelleme yolu aynı yuvarlamayı uygulamalı.
        using var o = new Ortam();
        var category = new Category { UserId = 1, Name = "Uydurma bütçe", Type = TransactionType.Expense };
        o.Db.Categories.Add(category); await o.Db.SaveChangesAsync();
        var dto = new CreateBudgetDto { CategoryId = category.Id, MonthlyLimit = 23.125m };
        var created = await o.Budgets.UpsertAsync(dto);
        Assert.Equal(23.13m, created.MonthlyLimit);
        Assert.Equal(created.MonthlyLimit, (await o.Db.Budgets.AsNoTracking().SingleAsync()).MonthlyLimit);
        dto.MonthlyLimit = 34.125m;
        var updated = await o.Budgets.UpsertAsync(dto);
        Assert.Equal(created.Id, updated.Id); Assert.Equal(34.13m, updated.MonthlyLimit);
        Assert.Equal(updated.MonthlyLimit, (await o.Db.Budgets.AsNoTracking().SingleAsync()).MonthlyLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Butce_KolonaSigmayanLimit_Reddedilir(bool update)
    {
        // NEDEN: InMemory'de görünmeyen kolon taşması bütçe oluşturma/güncellemede engellenmeli.
        using var o = new Ortam();
        var category = new Category { UserId = 1, Name = "Uydurma bütçe", Type = TransactionType.Expense };
        o.Db.Categories.Add(category); await o.Db.SaveChangesAsync();
        var dto = new CreateBudgetDto { CategoryId = category.Id, MonthlyLimit = 23.125m };
        if (update) await o.Budgets.UpsertAsync(dto);
        dto.MonthlyLimit = MoneyRules.Max + 0.01m;
        var ex = await Assert.ThrowsAsync<BadRequestException>(() => o.Budgets.UpsertAsync(dto));
        Assert.Equal("Tutar çok büyük.", ex.Message);
        if (update) Assert.Equal(23.13m, (await o.Db.Budgets.AsNoTracking().SingleAsync()).MonthlyLimit);
        else Assert.Empty(o.Db.Budgets);
    }
}
