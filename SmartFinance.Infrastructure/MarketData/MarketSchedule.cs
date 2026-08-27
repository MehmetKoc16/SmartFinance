namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Bir yatırım tipinin fiyatının şu anda değişip değişmediğini belirler.
///
/// Amaç: piyasa kapalıyken fiyat zaten sabit olduğu için dış servise istek
/// atmamak. BIST haftada ~40 saat açık (168 saatin ~%24'ü); geri kalan zamanda
/// yenileme yapmamak istek sayısını belirgin biçimde düşürüyor.
///
/// Kripto 7/24 işlem gördüğü için bu kısıt yalnızca borsa/döviz tiplerine uygulanır.
/// </summary>
public static class MarketSchedule
{
    // Türkiye 2016'dan beri kalıcı olarak UTC+3 (yaz saati uygulaması yok),
    // bu yüzden sabit ofset güvenli ve platformdan bağımsız. TimeZoneInfo ile
    // bölge adı aramak Windows/Linux arasında farklı isimlendirme sorunu çıkarır.
    private static readonly TimeSpan IstanbulOffset = TimeSpan.FromHours(3);

    // BIST seansı 10:00-18:00. Kapanış fiyatının da yakalanabilmesi için
    // pencere yarım saat uzatılıyor.
    private static readonly TimeSpan SessionStart = new(9, 30, 0);
    private static readonly TimeSpan SessionEnd = new(18, 30, 0);

    public static bool ShouldRefresh(string investmentType, DateTime utcNow)
    {
        // Kripto piyasası hiç kapanmaz.
        if (string.Equals(investmentType, "crypto", StringComparison.OrdinalIgnoreCase))
            return true;

        var istanbul = utcNow + IstanbulOffset;

        // Hafta sonu hiçbir borsa/kur verisi güncellenmez.
        if (istanbul.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var time = istanbul.TimeOfDay;
        return time >= SessionStart && time <= SessionEnd;
    }
}
