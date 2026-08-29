using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Piyasa kapaliyken fiyat degismedigi icin dis servise istek atilmamali.
/// Testler UTC verilir; Turkiye kalici olarak UTC+3 (yaz saati yok).
public class MarketScheduleTests
{
    // 2026-08-26 Carsamba
    private static DateTime UtcOnWednesday(int hour, int minute = 0)
        => new(2026, 8, 26, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(7, 0)]    // TR 10:00 — seans basi
    [InlineData(11, 0)]   // TR 14:00 — seans ortasi
    [InlineData(14, 59)]  // TR 17:59 — kapanisa yakin
    public void Hisse_SeansSaatlerinde_Yenilenir(int utcHour, int utcMinute)
    {
        Assert.True(MarketSchedule.ShouldRefresh("stock", UtcOnWednesday(utcHour, utcMinute)));
    }

    [Theory]
    [InlineData(3, 0)]    // TR 06:00 — acilistan once
    [InlineData(16, 0)]   // TR 19:00 — kapanistan sonra
    [InlineData(22, 0)]   // TR 01:00 — gece
    public void Hisse_SeansDisinda_Yenilenmez(int utcHour, int utcMinute)
    {
        Assert.False(MarketSchedule.ShouldRefresh("stock", UtcOnWednesday(utcHour, utcMinute)));
    }

    [Fact]
    public void Hisse_HaftaSonu_SeansSaatindeBileYenilenmez()
    {
        // 2026-08-29 Cumartesi, TR 14:00
        var cumartesi = new DateTime(2026, 8, 29, 11, 0, 0, DateTimeKind.Utc);
        Assert.False(MarketSchedule.ShouldRefresh("stock", cumartesi));
    }

    /// Kripto piyasasi hic kapanmaz — hafta sonu ve gece dahil her zaman yenilenmeli.
    [Theory]
    [InlineData(2026, 8, 26, 2)]   // Carsamba gece
    [InlineData(2026, 8, 29, 11)]  // Cumartesi ogle
    [InlineData(2026, 8, 30, 22)]  // Pazar gece
    public void Kripto_HerZamanYenilenir(int y, int m, int d, int utcHour)
    {
        var t = new DateTime(y, m, d, utcHour, 0, 0, DateTimeKind.Utc);
        Assert.True(MarketSchedule.ShouldRefresh("crypto", t));
    }

    /// Altin fiyati Binance PAXG/TRY paritesinden geliyor; o piyasa 7/24 acik.
    /// TCMB'nin aylik serisi kullanilirken bu gecerli degildi.
    [Theory]
    [InlineData(2026, 8, 26, 2)]   // Carsamba gece
    [InlineData(2026, 8, 29, 11)]  // Cumartesi ogle
    [InlineData(2026, 8, 30, 22)]  // Pazar gece
    public void Altin_HerZamanYenilenir(int y, int m, int d, int utcHour)
    {
        var t = new DateTime(y, m, d, utcHour, 0, 0, DateTimeKind.Utc);
        Assert.True(MarketSchedule.ShouldRefresh("gold", t));
    }

    [Fact]
    public void TipBuyukKucukHarfDuyarsiz()
    {
        var geceyarisi = UtcOnWednesday(2);
        Assert.True(MarketSchedule.ShouldRefresh("CRYPTO", geceyarisi));
        Assert.True(MarketSchedule.ShouldRefresh("Gold", geceyarisi));
        Assert.False(MarketSchedule.ShouldRefresh("STOCK", geceyarisi));
    }

    /// Kapanis fiyatinin yakalanabilmesi icin pencere seans sonundan biraz
    /// sonraya kadar aciktir.
    [Fact]
    public void Hisse_KapanistanHemenSonra_HalaYenilenir()
    {
        // TR 18:15 — seans bitti ama kapanis fiyati alinabilsin
        Assert.True(MarketSchedule.ShouldRefresh("stock", UtcOnWednesday(15, 15)));
    }
}
