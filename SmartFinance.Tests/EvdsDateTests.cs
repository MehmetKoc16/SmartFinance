using SmartFinance.Infrastructure.MarketData;

namespace SmartFinance.Tests;

/// Regresyon: EVDS'nin UNIXTIME alani ilgili gunun ISTANBUL gece yarisini
/// gosteriyor. Dogrudan UTC'ye cevirip .Date almak her tarihi bir gun geriye
/// kaydiriyordu — grafikteki son mum, fiyati dogru olsa bile bir onceki gune
/// etiketleniyordu. Hem gumus hem doviz serilerini etkiliyordu.
public class EvdsDateTests
{
    /// 29.08.2026'da canli EVDS yanitindan alinan gercek degerler.
    [Theory]
    [InlineData(1787605200, 2026, 8, 25)]  // Tarih alani "25-08-2026"
    [InlineData(1787691600, 2026, 8, 26)]  // Tarih alani "26-08-2026"
    [InlineData(1787778000, 2026, 8, 27)]  // Tarih alani "27-08-2026"
    public void IstanbulGeceYarisi_DogruGuneCozulur(long unix, int yil, int ay, int gun)
    {
        Assert.Equal(new DateTime(yil, ay, gun), EvdsDate.FromUnixSeconds(unix));
    }

    /// Duzeltme olmadan tarih bir gun geriye kayiyordu — bu testin amaci o
    /// davranisin geri gelmedigini gostermek.
    [Fact]
    public void DuzHamUtcCevrimi_BirGunGeriyeKayardi()
    {
        const long unix = 1787778000; // "27-08-2026"

        var hataliCevrim = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date;
        var dogruCevrim = EvdsDate.FromUnixSeconds(unix);

        Assert.Equal(new DateTime(2026, 8, 26), hataliCevrim);
        Assert.Equal(new DateTime(2026, 8, 27), dogruCevrim);
        Assert.Equal(1, (dogruCevrim - hataliCevrim).Days);
    }
}
