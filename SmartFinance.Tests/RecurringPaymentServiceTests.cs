using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.DTOs.RecurringPayment;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class RecurringPaymentServiceTests
{
    internal sealed class Saat : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public Saat(DateTimeOffset? now = null) => _now = now ?? new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    internal sealed class Ortam : IDisposable
    {
        public SmartFinanceDbContext Db { get; }
        public ICurrentUserService User { get; }
        public RecurringPaymentService Service { get; }
        public Ortam(string? databaseName = null, DateTimeOffset? now = null)
        {
            Db = new SmartFinanceDbContext(new DbContextOptionsBuilder<SmartFinanceDbContext>()
                .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            var user = new Mock<ICurrentUserService>(); user.SetupGet(x => x.UserId).Returns(1); User = user.Object;
            var transactions = new TransactionService(new GenericRepository<Transaction>(Db), Db, User, new NotificationService(Db, User));
            Service = new RecurringPaymentService(Db, User, new EntitlementService(Db, User, new ConfigurationBuilder().Build()), transactions, new Saat(now));
        }
        public void Dispose() => Db.Dispose();
        public static CreateRecurringPaymentDto Dto() => new()
        {
            Name = "Deneme Dijital Abonelik", Amount = 17.25m, Type = TransactionType.Expense,
            Frequency = RecurrenceFrequency.Monthly, StartDate = new DateOnly(2026, 10, 19)
        };
        public async Task<RecurringPayment> Kayit(int fark = 0, bool active = true)
        {
            var p = new RecurringPayment { UserId = 1, Name = "Deneme planı", Amount = 17.25m,
                Type = TransactionType.Expense, Frequency = RecurrenceFrequency.Weekly,
                StartDate = new DateOnly(2026, 10, 19).AddDays(fark), NextDueDate = new DateOnly(2026, 10, 19).AddDays(fark),
                AnchorDay = new DateOnly(2026, 10, 19).AddDays(fark).Day, IsActive = active };
            Db.RecurringPayments.Add(p); await Db.SaveChangesAsync(); return p;
        }
    }

    // NEDEN: Oluşturma geçmiş vadeleri gecikmiş borca çevirmemeli.
    [Fact]
    public async Task Olustur_GecmisBaslangic_IlkGelecekVadeyeIlerler()
    {
        using var o = new Ortam(); var dto = Ortam.Dto(); dto.StartDate = new DateOnly(2026, 1, 31);
        var p = await o.Service.CreateAsync(dto);
        Assert.Equal(new DateOnly(2026, 10, 31), p.NextDueDate); Assert.Equal(31, p.AnchorDay);
    }

    // NEDEN: Aktif üç kayıt ücretsiz sınırını doldurur.
    [Fact]
    public async Task Olustur_UcretsizDorduncu_402()
    {
        using var o = new Ortam(); for (var i = 0; i < 3; i++) await o.Service.CreateAsync(Ortam.Dto());
        await Assert.ThrowsAsync<PremiumRequiredException>(() => o.Service.CreateAsync(Ortam.Dto()));
    }

    // NEDEN: Arşivlenmiş hesaba yeni plan bağlanmamalı.
    [Fact]
    public async Task Olustur_ArsivliHesap_400()
    {
        using var o = new Ortam(); var a = new Account { UserId = 1, Name = "Deneme", IsArchived = true };
        o.Db.Accounts.Add(a); await o.Db.SaveChangesAsync(); var dto = Ortam.Dto(); dto.AccountId = a.Id;
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.CreateAsync(dto));
    }

    // NEDEN: Kategori türü işlem türüyle aynı olmalı.
    [Fact]
    public async Task Olustur_GiderOdemesineGelirKategorisi_400()
    {
        using var o = new Ortam(); var c = new Category { UserId = 1, Name = "Deneme", Type = TransactionType.Income };
        o.Db.Categories.Add(c); await o.Db.SaveChangesAsync(); var dto = Ortam.Dto(); dto.CategoryId = c.Id;
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.CreateAsync(dto));
    }

    // NEDEN: Tutar düzenlemek gecikmiş vadeyi kaybettirmemeli.
    [Fact]
    public async Task Guncelle_YalnizTutar_VadeKorunur()
    {
        using var o = new Ortam(); var p = await o.Kayit(-7); var dto = Ortam.Dto();
        dto.StartDate = p.StartDate; dto.Frequency = p.Frequency; dto.Amount = 21.75m;
        Assert.Equal(p.NextDueDate, (await o.Service.UpdateAsync(p.Id, dto)).NextDueDate);
    }

    // NEDEN: Yeni takvim, eski NextDueDate'ten önceki (işlenmiş sayılan) vadeye dönmemeli;
    // gecikmiş mevcut vade ise kaybolmamalı (madde 7, 20.10.2026).
    [Fact]
    public async Task Guncelle_FrekansDegisir_VadeYenidenHesaplanir()
    {
        using var o = new Ortam(); var p = await o.Kayit(-7); var dto = Ortam.Dto(); dto.StartDate = p.StartDate;
        Assert.Equal(new DateOnly(2026, 10, 12), (await o.Service.UpdateAsync(p.Id, dto)).NextDueDate);
    }

    // NEDEN: Tek dokunuş işlem bağlantılarını ve bütçe yan etkisini korumalı.
    [Fact]
    public async Task Ode_IslemOlusturur_HesapVeKategoriTasinir_VadeIlerler()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var a = new Account { UserId = 1, Name = "Deneme" }; var c = new Category { UserId = 1, Name = "Deneme", Type = TransactionType.Expense };
        o.Db.AddRange(a, c); await o.Db.SaveChangesAsync(); p.AccountId = a.Id; p.CategoryId = c.Id;
        o.Db.Budgets.Add(new Budget { UserId = 1, CategoryId = c.Id, MonthlyLimit = 1.25m }); await o.Db.SaveChangesAsync();
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate });
        Assert.Equal(a.Id, result.Transaction.AccountId); Assert.Equal(c.Id, result.Transaction.CategoryId);
        Assert.Equal(new DateOnly(2026, 10, 26), result.RecurringPayment.NextDueDate);
        Assert.Single(o.Db.Transactions); Assert.Single(o.Db.Notifications);
    }

    // NEDEN: Aynı vade ikinci işlem üretmemeli.
    [Fact]
    public async Task Ode_AyniVadeIkinciKez_409()
    {
        using var o = new Ortam(); var p = await o.Kayit(); var dto = new PayRecurringPaymentDto { DueDate = p.NextDueDate };
        await o.Service.PayAsync(p.Id, dto);
        Assert.Equal("Bu vade zaten işlendi.", (await Assert.ThrowsAsync<ConflictException>(() => o.Service.PayAsync(p.Id, dto))).Message);
        Assert.Single(o.Db.Transactions);
    }

    // NEDEN: Son ödeme planı bitirmeli, yeniden ödeme işlem üretmemeli.
    [Fact]
    public async Task Ode_EndDateSonrasi_PasifOlur()
    {
        using var o = new Ortam(); var p = await o.Kayit(); p.EndDate = p.NextDueDate; await o.Db.SaveChangesAsync();
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate });
        Assert.False(result.RecurringPayment.IsActive);
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate }));
    }

    // NEDEN: Atlamak para hareketi oluşturmamalı ve eski vade tekrar atlanamamalı.
    [Fact]
    public async Task Atla_IslemOlusturmadanIlerler()
    {
        using var o = new Ortam(); var p = await o.Kayit(); var dto = new ProcessRecurringPaymentDto { DueDate = p.NextDueDate };
        Assert.Equal(new DateOnly(2026, 10, 26), (await o.Service.SkipAsync(p.Id, dto)).NextDueDate);
        Assert.Empty(o.Db.Transactions); await Assert.ThrowsAsync<ConflictException>(() => o.Service.SkipAsync(p.Id, dto));
    }

    // NEDEN: Geçmiş bir kere, gelecek haftalık vadeler ise ayrı ayrı sayılmalı.
    [Fact]
    public async Task Yaklasan_HaftalikBirdenCokVade_VeGecikmisBirKez_Toplamlar()
    {
        using var o = new Ortam(); await o.Kayit(-7); var gelir = await o.Kayit(); gelir.Type = TransactionType.Income;
        await o.Db.SaveChangesAsync(); var result = await o.Service.UpcomingAsync(14);
        Assert.Equal(7, result.Items.Count); Assert.Single(result.Items.Where(x => x.Status == "overdue"));
        Assert.Equal(69m, result.ExpenseTotal); Assert.Equal(51.75m, result.IncomeTotal);
    }

    // NEDEN: Kimlik tahmini başka kullanıcının verisine erişim vermemeli.
    [Fact]
    public async Task BaskaKullanicininKaydi_404()
    {
        using var o = new Ortam(); var p = await o.Kayit(); p.UserId = 2; await o.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.UpdateAsync(p.Id, Ortam.Dto()));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.DeleteAsync(p.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate }));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Service.SkipAsync(p.Id, new ProcessRecurringPaymentDto { DueDate = p.NextDueDate }));
        Assert.Empty(await o.Service.ListAsync());
    }

    // NEDEN: Mobil durum etiketleri TSİ günü ve aktiflikten türetilmeli.
    [Fact]
    public async Task Liste_DurumAlanlari()
    {
        using var o = new Ortam(); await o.Kayit(-1); await o.Kayit(); await o.Kayit(1); await o.Kayit(2, false);
        var list = await o.Service.ListAsync();
        Assert.Equal(new[] { "overdue", "today", "upcoming", "ended" }, list.Select(x => x.Status));
        Assert.Equal(new[] { -1, 0, 1, 2 }, list.Select(x => x.DaysUntil));
    }

    // NEDEN: Hesap kaldırıldığında silinmiş planların FK bağı da temizlenmeli.
    [Fact]
    public async Task HesapKaliciSilinince_PlanliOdemeHesabiBosalir()
    {
        using var o = new Ortam(); var a = new Account { UserId = 1, Name = "Deneme" }; o.Db.Accounts.Add(a);
        await o.Db.SaveChangesAsync(); var p = await o.Kayit(); p.AccountId = a.Id; p.IsDeleted = true; await o.Db.SaveChangesAsync();
        Assert.False(await new AccountService(o.Db, o.User, new EntitlementService(o.Db, o.User, new ConfigurationBuilder().Build())).DeleteAsync(a.Id));
        Assert.Null(o.Db.RecurringPayments.IgnoreQueryFilters().Single().AccountId);
    }

    // NEDEN: Kalıcı kullanıcı silme görünmeyen planları da kaldırmalı ve diğer kullanıcıyı korumalı.
    [Fact]
    public async Task KullaniciSilinince_PlanliOdemelerSilinir()
    {
        using var o = new Ortam(); o.Db.Users.Add(new User { Id = 1, FullName = "Deneme", Email = "dummy@example.invalid", PasswordHash = BCrypt.Net.BCrypt.HashPassword("Deneme123!") });
        var p = await o.Kayit(); p.IsDeleted = true; var other = await o.Kayit(); other.UserId = 2;
        await o.Db.SaveChangesAsync();
        var auth = new AuthService(o.Db, new ConfigurationBuilder().Build(), o.User, new FakeEmailSender(), NullLogger<AuthService>.Instance);
        await auth.DeleteAccountAsync(new DeleteAccountDto { Password = "Deneme123!" });
        Assert.Empty(o.Db.RecurringPayments.IgnoreQueryFilters().Where(x => x.UserId == 1));
        Assert.Single(o.Db.RecurringPayments.Where(x => x.UserId == 2));
    }

    // NEDEN: İki request aynı vadeyi önceden okumuşsa ikinci güncelleme 409 olmalı.
    [Fact]
    public async Task Atla_EszamanliEskiVade_409()
    {
        var database = Guid.NewGuid().ToString(); using var first = new Ortam(database); using var second = new Ortam(database);
        var p = await first.Kayit(); var due = p.NextDueDate;
        await second.Db.RecurringPayments.SingleAsync();
        await first.Service.SkipAsync(p.Id, new ProcessRecurringPaymentDto { DueDate = due });
        await Assert.ThrowsAsync<ConflictException>(() => second.Service.SkipAsync(p.Id, new ProcessRecurringPaymentDto { DueDate = due }));
    }

    // NEDEN: Model bağlama dışındaki servis çağrıları da geçersiz alanları reddetmeli.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Olustur_GecersizAlanlar_400(int scenario)
    {
        using var o = new Ortam(); var dto = Ortam.Dto();
        switch (scenario)
        {
            case 0: dto.Name = " "; break;
            case 1: dto.Name = new string('a', 101); break;
            case 2: dto.Amount = 0; break;
            case 3: dto.RemindDaysBefore = -1; break;
            case 4: dto.RemindDaysBefore = 8; break;
            case 5: dto.EndDate = dto.StartDate.AddDays(-1); break;
            case 6: dto.Type = (TransactionType)9; break;
            case 7: dto.Frequency = (RecurrenceFrequency)9; break;
            case 8: dto.StartDate = default; break;
        }
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.CreateAsync(dto));
        Assert.Empty(o.Db.RecurringPayments);
    }

    // NEDEN: Arşivlenmiş hesap veya eksik vade işlem ve takvimi değiştirmemeli.
    [Fact]
    public async Task Ode_GecersizAlanlar_IslemVeVadeKorunur()
    {
        using var o = new Ortam(); var p = await o.Kayit(); var due = p.NextDueDate;
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto()));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = due, Amount = -1 }));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = due, TransactionDate = default(DateTime) }));
        var a = new Account { UserId = 1, Name = "Deneme", IsArchived = true };
        o.Db.Accounts.Add(a); await o.Db.SaveChangesAsync(); p.AccountId = a.Id; await o.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = due }));
        Assert.Empty(o.Db.Transactions); Assert.Equal(due, p.NextDueDate);
    }

    // NEDEN: Ödeme anındaki opsiyonel değerler yalnız oluşturulan işleme uygulanmalı.
    [Fact]
    public async Task Ode_OverrideAlanlari_PlanSabitKalir()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var a = new Account { UserId = 1, Name = "Deneme" }; o.Db.Accounts.Add(a); await o.Db.SaveChangesAsync();
        // ZAMAN KURALI: işlem tarihi TSİ duvar saatidir (Kind belirsiz) ve aynen saklanır.
        var date = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Unspecified);
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto
            { DueDate = p.NextDueDate, Amount = 21.75m, AccountId = a.Id, Description = "Deneme ödeme", TransactionDate = date });
        Assert.Equal(21.75m, result.Transaction.Amount); Assert.Equal(a.Id, result.Transaction.AccountId);
        Assert.Equal("Deneme ödeme", result.Transaction.Description); Assert.Equal(date, result.Transaction.TransactionDate);
        Assert.Equal(17.25m, result.RecurringPayment.Amount); Assert.Null(result.RecurringPayment.AccountId);
    }

    // NEDEN: Soft-delete geçmiş işlemi korumalı ve bütün plan sorgularından çıkmalı.
    [Fact]
    public async Task Sil_SoftDelete_GecmisIslemKorunur()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate });
        await o.Service.DeleteAsync(p.Id);
        Assert.Empty(await o.Service.ListAsync()); Assert.Empty((await o.Service.UpcomingAsync()).Items);
        Assert.True(o.Db.RecurringPayments.IgnoreQueryFilters().Single().IsDeleted); Assert.Single(o.Db.Transactions);
    }

    // NEDEN: Pencere büyüklüğü ve plan bitişi listeyi sınırlamalı.
    [Fact]
    public async Task Yaklasan_BitisVeGunSiniri()
    {
        using var o = new Ortam(); var p = await o.Kayit(); p.EndDate = p.NextDueDate.AddDays(7); await o.Db.SaveChangesAsync();
        Assert.Equal(2, (await o.Service.UpcomingAsync(90)).Items.Count);
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.UpcomingAsync(0));
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.UpcomingAsync(91));
    }

    // ---- v16 yayın öncesi düzeltmeler (20.10.2026) ----

    // NEDEN: 00:00–03:00 TSİ arasındaki ödeme UTC şimdiyle önceki güne/aya düşüyordu.
    // 22:30 UTC = ertesi gün 01:30 TSİ duvar saati.
    [Fact]
    public async Task Ode_TarihVerilmezse_TsiSimdi()
    {
        using var o = new Ortam(now: new DateTimeOffset(2026, 10, 19, 22, 30, 0, TimeSpan.Zero)); var p = await o.Kayit();
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate });
        var expected = new DateTime(2026, 10, 20, 1, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(expected, result.Transaction.TransactionDate);
        Assert.Equal(DateTimeKind.Unspecified, result.Transaction.TransactionDate.Kind);
        Assert.Equal(expected, o.Db.Transactions.Single().TransactionDate);
    }

    // NEDEN: Mobil işlem tarihini duvar saati (Kind belirsiz) gönderir; 400 verilmemeli, kaydırılmamalı.
    [Fact]
    public async Task Ode_DuvarSaatiTarih_AynenKaydedilir()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var date = new DateTime(2026, 10, 19, 0, 30, 0, DateTimeKind.Unspecified);
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate, TransactionDate = date });
        Assert.Equal(date, result.Transaction.TransactionDate);
        Assert.Equal(date, o.Db.Transactions.Single().TransactionDate);
    }

    // NEDEN: Eski/yanlış istemci 'Z' gönderse de işlem doğru TSİ gününe düşmeli.
    [Fact]
    public async Task Ode_UtcTarih_TsiyeCevrilir()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate,
            TransactionDate = new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc) });
        Assert.Equal(new DateTime(2026, 10, 19, 1, 0, 0), result.Transaction.TransactionDate);
        Assert.Equal(DateTimeKind.Unspecified, result.Transaction.TransactionDate.Kind);
    }

    // NEDEN: EF int 0'ı 'değer yok' sayıp SQL varsayılanını (1) yazıyordu; 'Günü gelince' (0)
    // seçen kullanıcı bir gün önce bildirim alıyordu. InMemory bunu yakalamaz; model doğrulanır.
    [Fact]
    public void PlanliOdeme_RemindDaysBefore_VeritabaniVarsayilaniYok()
    {
        using var o = new Ortam();
        var model = o.Db.GetService<IDesignTimeModel>().Model;
        var property = model.FindEntityType(typeof(RecurringPayment))!.FindProperty(nameof(RecurringPayment.RemindDaysBefore))!;
        // GetDefaultValue() EF 10'da tanımsızken CLR varsayılanını (0) döndürüyor; asıl kanıt annotation'ın yokluğu.
        Assert.Null(property.FindAnnotation(RelationalAnnotationNames.DefaultValue));
        Assert.Null(property.GetDefaultValueSql());
        Assert.Equal(1, new RecurringPayment().RemindDaysBefore);
    }

    // NEDEN: Pasif planı güncellemeyle aktife çevirmek ücretsiz sınırı atlatmamalı.
    [Fact]
    public async Task Guncelle_PasifiAktifeCevirme_UcretsizSinirdaysa402()
    {
        using var o = new Ortam(); for (var i = 0; i < 3; i++) await o.Service.CreateAsync(Ortam.Dto());
        var p = await o.Kayit(-14, false); p.EndDate = p.StartDate; await o.Db.SaveChangesAsync();
        var dto = Ortam.Dto(); dto.StartDate = p.StartDate; dto.Frequency = RecurrenceFrequency.Monthly; dto.EndDate = null;
        var ex = await Assert.ThrowsAsync<PremiumRequiredException>(() => o.Service.UpdateAsync(p.Id, dto));
        Assert.Equal("Ücretsiz planda en fazla 3 planlı ödeme tanımlayabilirsiniz. Sınırsız planlı ödeme için Premium'a geçin.", ex.Message);
        Assert.False(o.Db.RecurringPayments.AsNoTracking().Single(x => x.Id == p.Id).IsActive);
    }

    // NEDEN: Aktif planın güncellenmesi sınırda olsa da engellenmemeli (yalnız pasif→aktif geçişi sayılır).
    [Fact]
    public async Task Guncelle_AktifPlan_UcretsizSinirdaEngellenmez()
    {
        using var o = new Ortam(); for (var i = 0; i < 3; i++) await o.Service.CreateAsync(Ortam.Dto());
        var id = o.Db.RecurringPayments.First().Id; var dto = Ortam.Dto(); dto.Frequency = RecurrenceFrequency.Weekly;
        Assert.True((await o.Service.UpdateAsync(id, dto)).IsActive);
    }

    // NEDEN: Kategori silindi diye ödeme 400 vermemeli; mevcut geri alma kalıbı gibi kategorisiz işlenir.
    [Fact]
    public async Task Ode_KategoriSilinmis_KategorisizIslemOlusur()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var c = new Category { UserId = 1, Name = "Deneme", Type = TransactionType.Expense };
        o.Db.Categories.Add(c); await o.Db.SaveChangesAsync(); p.CategoryId = c.Id; c.IsDeleted = true; await o.Db.SaveChangesAsync();
        var result = await o.Service.PayAsync(p.Id, new PayRecurringPaymentDto { DueDate = p.NextDueDate });
        Assert.Null(result.Transaction.CategoryId); Assert.Null(result.RecurringPayment.CategoryId);
        Assert.Null(o.Db.Transactions.Single().CategoryId);
        Assert.Null(o.Db.RecurringPayments.AsNoTracking().Single().CategoryId);
    }

    // NEDEN: İstemci planın (silinmiş) mevcut kategorisini geri gönderdiğinde güncelleme 400 vermemeli.
    [Fact]
    public async Task Guncelle_KategoriSilinmis_KategoriBosalir()
    {
        using var o = new Ortam(); var p = await o.Kayit();
        var c = new Category { UserId = 1, Name = "Deneme", Type = TransactionType.Expense };
        o.Db.Categories.Add(c); await o.Db.SaveChangesAsync(); p.CategoryId = c.Id; c.IsDeleted = true; await o.Db.SaveChangesAsync();
        var dto = Ortam.Dto(); dto.StartDate = p.StartDate; dto.Frequency = p.Frequency; dto.CategoryId = c.Id; dto.Amount = 21.75m;
        var result = await o.Service.UpdateAsync(p.Id, dto);
        Assert.Null(result.CategoryId); Assert.Equal(21.75m, result.Amount);
        Assert.Null(o.Db.RecurringPayments.AsNoTracking().Single().CategoryId);
        // Planın kategorisi olmayan başka bir (silinmiş/yabancı) kimlik yine 400.
        var other = new Category { UserId = 2, Name = "Başka", Type = TransactionType.Expense };
        o.Db.Categories.Add(other); await o.Db.SaveChangesAsync(); dto.CategoryId = other.Id;
        await Assert.ThrowsAsync<BadRequestException>(() => o.Service.UpdateAsync(p.Id, dto));
    }

    // NEDEN: Ödenmiş aylık plan haftalığa çevrilince takvim baştan hesaplanıp ödenmiş döneme dönüyordu.
    [Fact]
    public async Task Guncelle_OdenmisPlanHaftaligaCevrilir_VadeGeriGitmez()
    {
        using var o = new Ortam(); var created = await o.Service.CreateAsync(Ortam.Dto());
        var paid = await o.Service.PayAsync(created.Id, new PayRecurringPaymentDto { DueDate = created.NextDueDate });
        Assert.Equal(new DateOnly(2026, 11, 19), paid.RecurringPayment.NextDueDate);
        var dto = Ortam.Dto(); dto.Frequency = RecurrenceFrequency.Weekly;
        // Haftalık takvim 19.10'dan: 19.10, 26.10, ..., 16.11, 23.11 → eski vadeden (19.11) küçük olmayan ilk vade.
        var result = await o.Service.UpdateAsync(created.Id, dto);
        Assert.Equal(new DateOnly(2026, 11, 23), result.NextDueDate);
        Assert.True(result.IsActive);
    }
}
