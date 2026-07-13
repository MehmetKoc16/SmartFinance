namespace SmartFinance.Infrastructure.MarketData;

public static class TcmbSeriesMap
{
    // "S" (satış) kuru kullanılıyor — elde tutulan varlığın konservatif değeri için
    public static readonly Dictionary<string, string> CurrencySeriesCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "USD", "TP.DK.USD.S.YTL" },
        { "EUR", "TP.DK.EUR.S.YTL" },
        { "GBP", "TP.DK.GBP.S.YTL" },
    };

    // EVDS katalogundan doğrulandı (bie_mkaltytl grubu) — ama bu seri AYLIK, günlük değil.
    // 180 günlük pencerede ~6 veri noktası döner; RSI/MACD/Bollinger için yetersiz kalır.
    public static readonly Dictionary<string, string> GoldSeriesCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "GRAM ALTIN", "TP.MK.KUL.YTL" },
        { "ALTIN", "TP.MK.KUL.YTL" },
    };
}
