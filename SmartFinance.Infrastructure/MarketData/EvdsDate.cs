namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// TCMB EVDS yanıtlarındaki tarih alanının çözümlenmesi.
///
/// EVDS'nin UNIXTIME alanı, ilgili günün İSTANBUL gece yarısını gösteriyor.
/// Doğrudan UTC'ye çevirip <c>.Date</c> almak her tarihi bir gün geriye
/// kaydırıyordu — örnek (29.08.2026'da ölçüldü):
///
///   Tarih alanı "27-08-2026" -> UNIXTIME 2026-08-26T21:00:00Z -> .Date 2026-08-26
///
/// Yani grafikteki son mum, fiyatı doğru olsa bile bir önceki güne
/// etiketleniyordu. Hata hem gümüş hem döviz serilerini etkiliyordu.
///
/// Neden sabit ofset: Türkiye 2016'dan beri kalıcı olarak UTC+3, yaz saati
/// uygulaması yok. TimeZoneInfo ile bölge adı aramak Windows/Linux arasında
/// farklı isimlendirme sorunu çıkarır (aynı gerekçe MarketSchedule'da da var).
/// </summary>
public static class EvdsDate
{
    private static readonly TimeSpan IstanbulOffset = TimeSpan.FromHours(3);

    public static DateTime FromUnixSeconds(long unixSeconds) =>
        (DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime + IstanbulOffset).Date;
}
