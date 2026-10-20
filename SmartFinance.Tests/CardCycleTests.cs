using SmartFinance.Infrastructure.Accounts;

namespace SmartFinance.Tests;

public class CardCycleTests
{
    // NEDEN: Harcamasız geçmiş dönem ödeme yapılmış sayılmamalı; açık dönem open kalmalı.
    [Fact]
    public void Ekstre_HarcamasizGecmisDonem_EmptyDurumu()
    {
        var result = Hesapla(Array.Empty<CardCycle.Entry>());

        Assert.All(result.Past, past =>
        {
            Assert.Equal("empty", past.Status);
            Assert.Equal(0m, past.Amount);
            Assert.Equal(0m, past.Paid);
            Assert.Equal(0m, past.Remaining);
        });
        Assert.Equal("open", result.Current.Status);
    }

    // NEDEN: İadeyle net sıfır dönem empty olur; negatif tutarlı dönemin mevcut paid durumu korunur.
    [Theory]
    [InlineData(-100, "empty")]
    [InlineData(-120, "paid")]
    public void Ekstre_IadeSonrasiDurum_NetTutaraGoreBelirlenir(int refund, string status)
    {
        var result = Hesapla(new[] { Harcama(10, 1, 100), Harcama(10, 2, refund) }, payment: 45);

        Assert.Equal(status, result.Past[0].Status);
        Assert.Equal(0m, result.Past[0].Paid);
        Assert.Equal(0m, result.Past[0].Remaining);
    }

    // NEDEN: Şubat kesimi geçerli takvim gününde kalmalı.
    [Fact]
    public void KesimTarihi_KisaAydaAyinSonGunu() =>
        Assert.Equal(new DateOnly(2027, 2, 28), CardCycle.StatementDate(2027, 2, 28));

    // NEDEN: Önceki kesim iki kez sayılmamalı, bu kesim kaybolmamalı.
    [Fact]
    public void Donem_OncekiKesiminErtesiGunuDahil_KesimGunuDahil()
    {
        var result = Hesapla(new[] { Harcama(9, 20, 10), Harcama(9, 21, 20), Harcama(10, 20, 30) });
        var past = result.Past[0];
        Assert.Equal("2026-09-21", past.PeriodStart);
        Assert.Equal("2026-10-20", past.PeriodEnd);
        Assert.Equal(50m, past.Amount);
    }

    // NEDEN: Büyük ödeme günü aynı ayda olmalı.
    [Fact]
    public void SonOdeme_DueDayKesimdenBuyukseAyniAy() =>
        Assert.Equal(new DateOnly(2026, 10, 25), CardCycle.DueDate(new(2026, 10, 20), 25));

    // NEDEN: Küçük veya eşit ödeme günü sonraki aya geçmeli.
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public void SonOdeme_DueDayKucukseErtesiAy(int day) =>
        Assert.Equal(new DateOnly(2026, 11, day), CardCycle.DueDate(new(2026, 10, 20), day));

    // NEDEN: Transfer (ödeme) UTC'dir; UTC'de bugün geç saatteki ödeme TSİ'de yarına düşer ve
    // bugünün borcunu kapatmaz. 20:30 UTC = 23:30 TSİ (bugün), 21:30 UTC = 00:30 TSİ (yarın).
    // ZAMAN KURALI (20.10.2026): bu dönüşüm yalnız transferler içindir; işlem günü dönüştürülmez.
    [Fact]
    public void TsiGunSiniri_UtcGeceYarisiSonrasiTransferDogruGune()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(10, 1, 100) },
            new[]
            {
                new CardCycle.Entry(new DateTime(2026, 10, 22, 20, 30, 0, DateTimeKind.Utc), 40),
                new CardCycle.Entry(new DateTime(2026, 10, 22, 21, 30, 0, DateTimeKind.Utc), 50)
            },
            new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(40m, r.Past[0].Paid);
        Assert.Equal(60m, r.Past[0].Remaining);
    }

    // NEDEN: İşlem tarihi TSİ duvar saatidir; kesim günü 22:00'de girilen harcama UTC sanılıp
    // ertesi güne (açık döneme) kaydırılmamalı.
    [Fact]
    public void Ekstre_KesimGunu22de_GirilenIslem_AyniEkstrede()
    {
        var result = Hesapla(new[] { new CardCycle.Entry(new DateTime(2026, 10, 20, 22, 0, 0, DateTimeKind.Unspecified), 75) });
        Assert.Equal("2026-10-20", result.Past[0].PeriodEnd);
        Assert.Equal(75m, result.Past[0].Amount);
        Assert.Equal(0m, result.Current.Amount);
    }

    // NEDEN: 'Gelecek günlü hareket bugünün borcuna katılmaz' kuralı işlemde duvar saati gününe bakar:
    // bugün (TSİ 22.10) 23:30 duvar saatli harcama sayılır, ertesi gün 00:30 sayılmaz.
    [Fact]
    public void GelecekGunluIslem_DuvarSaatiGunuyleAyiklanir()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[]
            {
                new CardCycle.Entry(new DateTime(2026, 10, 22, 23, 30, 0, DateTimeKind.Unspecified), 30),
                new CardCycle.Entry(new DateTime(2026, 10, 23, 0, 30, 0, DateTimeKind.Unspecified), 40)
            },
            Array.Empty<CardCycle.Entry>(), new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(30m, r.Current.Amount);
    }

    // NEDEN: Açılış borcu ve eski ekstre yeni ekstreden önce kapanmalı.
    [Fact]
    public void OdemelerFifo_EnEskiEkstreyiOnceKapatir()
    {
        var r = Hesapla(new[] { Harcama(9, 1, 100), Harcama(10, 1, 200) }, 50, 120);
        Assert.Equal(70m, r.Past[1].Paid);
        Assert.Equal(30m, r.Past[1].Remaining);
        Assert.Equal(0m, r.Past[0].Paid);
    }

    // NEDEN: Fazla ödeme eski borç bittikten sonra yeni ve açık döneme geçmeli.
    [Fact]
    public void FazlaOdeme_SonrakiEkstreyeGecer()
    {
        var r = Hesapla(new[] { Harcama(9, 1, 100), Harcama(10, 1, 200), Harcama(10, 21, 80) }, 50, 400);
        Assert.Equal(100m, r.Past[1].Paid);
        Assert.Equal(200m, r.Past[0].Paid);
        Assert.Equal(50m, r.Current.Paid);
        Assert.Equal(30m, r.Current.Remaining);
        Assert.Equal("open", r.Current.Status);
    }

    // NEDEN: Son ödeme günü geçmiş kalan borç gecikmiş görünmeli.
    [Fact]
    public void Gecikti_SonOdemeGectiVeKalanVar()
    {
        var r = Hesapla(new[] { Harcama(9, 1, 100) });
        Assert.Equal("overdue", r.Past[1].Status);
    }

    // NEDEN: Son ödeme gününde gecikme başlamamalı; durumlar kalan borca bağlı olmalı.
    [Theory]
    [InlineData(0, "unpaid", 100)]
    [InlineData(40, "partial", 60)]
    [InlineData(100, "paid", 0)]
    public void EkstreDurumu_OdenenTutaraGoreBelirlenir(int payment, string status, int remaining)
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(10, 1, 100) },
            new[] { Odeme(10, 25, payment) }, new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(status, r.Past[0].Status);
        Assert.Equal((decimal)remaining, r.Past[0].Remaining);
    }

    // NEDEN: Gösterilmeyen eski dönemler de ödeme sırasına katılmalı.
    [Fact]
    public void Fifo_GosterilenDonemSayisindanBagimsizdir()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(1, 1, 100), Harcama(10, 1, 200) },
            new[] { Odeme(10, 21, 150) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), 1);
        Assert.Equal(50m, Assert.Single(r.Past).Paid);
        Assert.Equal(150m, r.Past[0].Remaining);
    }

    // NEDEN: İade dönem harcamasını azaltmalı; gelecekteki ödeme bugünkü borcu kapatmamalı.
    [Fact]
    public void Iade_NetHarcamayiAzaltir_GelecekOdemeSayilmaz()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(10, 1, 100), Harcama(10, 2, -30) },
            new[] { Odeme(10, 23, 70) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(70m, r.Past[0].Amount);
        Assert.Equal(0m, r.Past[0].Paid);
        Assert.Equal(70m, r.Past[0].Remaining);
    }

    // ZAMAN KURALI: harcama (Transaction) TSİ duvar saatidir; ödeme (Transfer) UTC'dir.
    private static CardCycle.Entry Harcama(int month, int day, decimal amount) =>
        new(new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Unspecified), amount);

    private static CardCycle.Entry Odeme(int month, int day, decimal amount) =>
        new(new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc), amount);

    private static SmartFinance.Application.DTOs.Account.StatementsDto Hesapla(
        CardCycle.Entry[] entries, decimal opening = 0, decimal payment = 0) =>
        CardCycle.Calculate(20, 25, opening, entries,
            new[] { Odeme(10, 21, payment) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), 3);
}
