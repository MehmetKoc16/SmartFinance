using SmartFinance.Infrastructure.Fundamentals;

namespace SmartFinance.Tests;

/// <summary>
/// NEDEN: Sunucuyu paylasan otomatik-al-sat projesi hafta ici 20:00 TSI'de KAP'tan
/// toplu ZIP cekiyor (18:30-23:30 TSI penceresi). Deploy saatine gore kayan 6 saatlik
/// periyot o pencereye denk gelip IP engeline (429/403) yol actigi icin KAP senkronu
/// her gun sabit 05:00 ve 13:00 Europe/Istanbul (02:00 ve 10:00 UTC) saatlerinde kosmali.
/// </summary>
public class KapSyncScheduleTests
{
    // NEDEN: Gece/sabah 05:00 TSI'den (02:00 UTC) onceki bir anda (01:00 UTC = 04:00 TSI)
    // siradaki ilk calisma ayni gun 05:00 TSI (02:00 UTC) olmalidir.
    [Fact]
    public void SabahOnceSaatAltiOlmadan_SonrakiTur_0500()
    {
        var nowUtc = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        var beklenenUtc = new DateTime(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);

        var sonucUtc = KapFundamentalsSyncService.NextRunUtc(nowUtc);

        Assert.Equal(beklenenUtc, sonucUtc);
    }

    // NEDEN: Tam 05:00 TSI (02:00 UTC) aninda cagrildiginda sifir bekleme ile ayni turu
    // tekrar calistirmamak icin bir sonraki tur olan 13:00 TSI (10:00 UTC) donmelidir.
    [Fact]
    public void TamBesteYse_SonrakiTur_13()
    {
        var nowUtc = new DateTime(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);
        var beklenenUtc = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

        var sonucUtc = KapFundamentalsSyncService.NextRunUtc(nowUtc);

        Assert.Equal(beklenenUtc, sonucUtc);
    }

    // NEDEN: 13:00 TSI (10:00 UTC) gectikten sonra (15:00 UTC = 18:00 TSI) aksam saatlerinde
    // tur acilmamali, ertesi gun 05:00 TSI (02:00 UTC) beklenmelidir.
    [Fact]
    public void OgledenSonra_SonrakiTur_ErtesiGun0500()
    {
        var nowUtc = new DateTime(2026, 10, 19, 15, 0, 0, DateTimeKind.Utc);
        var beklenenUtc = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);

        var sonucUtc = KapFundamentalsSyncService.NextRunUtc(nowUtc);

        Assert.Equal(beklenenUtc, sonucUtc);
    }

    // NEDEN: Bir hafta boyunca hangi anda (her 7 dakikada bir) olursa olsun planlanan
    // bir sonraki tur asla otomatik-al-sat'in 18:30-23:30 TSI KAP penceresine dusmemelidir.
    [Fact]
    public void AksamPenceresine_HicDusmez()
    {
        var baslangicUtc = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var bitisUtc = baslangicUtc.AddDays(7);
        var yasakBaslangicTsi = new TimeOnly(18, 30);
        var yasakBitisTsi = new TimeOnly(23, 30);

        for (var nowUtc = baslangicUtc; nowUtc < bitisUtc; nowUtc = nowUtc.AddMinutes(7))
        {
            var nextUtc = KapFundamentalsSyncService.NextRunUtc(nowUtc);
            Assert.True(nextUtc > nowUtc);

            // TSI = UTC+3 (sabit)
            var nextTsiSaat = TimeOnly.FromDateTime(nextUtc.AddHours(3));
            Assert.False(
                nextTsiSaat >= yasakBaslangicTsi && nextTsiSaat <= yasakBitisTsi,
                $"NextRunUtc ({nextUtc:O}) yasakli 18:30-23:30 TSI penceresine dustu: {nextTsiSaat}");
        }
    }

    // NEDEN: Yil sonu 31 Aralik ogleden sonra (16:00 UTC = 19:00 TSI) bir sonraki tur
    // yeni yilin ilk gunu 01 Ocak 05:00 TSI (02:00 UTC) olarak dogru hesaplanmalidir.
    [Fact]
    public void YilSonu_GunDegisimi()
    {
        var nowUtc = new DateTime(2026, 12, 31, 16, 0, 0, DateTimeKind.Utc);
        var beklenenUtc = new DateTime(2027, 1, 1, 2, 0, 0, DateTimeKind.Utc);

        var sonucUtc = KapFundamentalsSyncService.NextRunUtc(nowUtc);

        Assert.Equal(beklenenUtc, sonucUtc);
    }
}
