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

    // Not: altin serisi 29.08.2026'da kaldirildi. TCMB'nin TP.MK.KUL.YTL serisi
    // AYLIK ve o tarihte en yeni verisi Mayis 2026'ydi; gram altin artik
    // GoldPriceProvider icinde Binance PAXG/TRY paritesinden turetiliyor.
}
