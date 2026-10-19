using SmartFinance.Infrastructure.Accounts;

namespace SmartFinance.Tests;

public class CardCycleTests
{
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

    // NEDEN: UTC'de kesim günündeki geç saat TSİ'de açık döneme aittir.
    [Fact]
    public void TsiGunSiniri_UtcGeceYarisiSonrasiIslemDogruDoneme()
    {
        var result = Hesapla(new[] { new CardCycle.Entry(new DateTime(2026, 10, 20, 21, 30, 0, DateTimeKind.Utc), 75) });
        Assert.Equal(75m, result.Current.Amount);
        Assert.Equal(0m, result.Past[0].Amount);
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
            new[] { Harcama(10, 25, payment) }, new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(status, r.Past[0].Status);
        Assert.Equal((decimal)remaining, r.Past[0].Remaining);
    }

    // NEDEN: Gösterilmeyen eski dönemler de ödeme sırasına katılmalı.
    [Fact]
    public void Fifo_GosterilenDonemSayisindanBagimsizdir()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(1, 1, 100), Harcama(10, 1, 200) },
            new[] { Harcama(10, 21, 150) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), 1);
        Assert.Equal(50m, Assert.Single(r.Past).Paid);
        Assert.Equal(150m, r.Past[0].Remaining);
    }

    // NEDEN: İade dönem harcamasını azaltmalı; gelecekteki ödeme bugünkü borcu kapatmamalı.
    [Fact]
    public void Iade_NetHarcamayiAzaltir_GelecekOdemeSayilmaz()
    {
        var r = CardCycle.Calculate(20, 25, 0, new[] { Harcama(10, 1, 100), Harcama(10, 2, -30) },
            new[] { Harcama(10, 23, 70) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(70m, r.Past[0].Amount);
        Assert.Equal(0m, r.Past[0].Paid);
        Assert.Equal(70m, r.Past[0].Remaining);
    }

    private static CardCycle.Entry Harcama(int month, int day, decimal amount) =>
        new(new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Utc), amount);

    private static SmartFinance.Application.DTOs.Account.StatementsDto Hesapla(
        CardCycle.Entry[] entries, decimal opening = 0, decimal payment = 0) =>
        CardCycle.Calculate(20, 25, opening, entries,
            new[] { Harcama(10, 21, payment) }, new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), 3);
}
