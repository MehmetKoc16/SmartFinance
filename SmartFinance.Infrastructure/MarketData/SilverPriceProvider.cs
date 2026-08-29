using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Gram gümüş fiyatı — TCMB EVDS üzerinden BİST Kıymetli Madenler Piyasası'nın
/// resmî kapanış fiyatı (TP.GUMUSPIYASA.KAP02, TL/kg).
///
/// Neden altındaki gibi Binance değil: Binance'te (ve genel olarak büyük kripto
/// borsalarında) gümüş tokeni yok — altın için PAXG/XAUT var, gümüşün karşılığı
/// yok. Yahoo'nun SI=F vadeli sözleşmesinden türetmek denendi ama resmî BİST
/// kapanışına göre ±%1,5 bandında, üstelik işaret değiştiren bir sapma veriyor
/// (vade primi değil, kapanış saatlerinin farklı olması). Türk kullanıcının
/// karşılaştıracağı fiyat BİST fiyatı olduğu için resmî seri tercih edildi.
///
/// Sınırları (dürüstlük payı):
/// - Yalnızca KAPANIŞ fiyatı var; OHLC alanlarının dördü de aynı değeri taşır,
///   hacim yayınlanmaz. Altın ve kriptonun aksine mum grafiği çizilemez.
/// - İş günü frekansında: güncel fiyat, son işlem gününün kapanışıdır.
///   Hafta sonu ve tatillerde bir önceki kapanış görünür.
/// Ölçüldü (29.08.2026): Temmuz-Ağustos'ta 35 veri noktası, en yenisi 27 Ağustos.
/// </summary>
public class SilverPriceProvider : IPriceProvider, IHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IPriceHistoryStore _historyStore;

    private const string InvestmentType = "silver";

    // TL/kg yayınlanıyor; kullanıcı gram üzerinden tutuyor.
    private const decimal GramsPerKilogram = 1000m;

    // TL/gr serisi de var (TP.GUMUSPIYASA.KAP05) ama 29.08.2026'da en yeni
    // verisi 5 Ağustos'tu; kg serisi 27 Ağustos'a kadar günceldi.
    private const string SeriesCode = "TP.GUMUSPIYASA.KAP02";

    // EVDS'nin hız sınırı belgelenmemiş; senkron işi günde bir çalıştığı için
    // ihtiyatlı bir ara yeterli.
    public TimeSpan InterSymbolDelay => TimeSpan.FromSeconds(2);

    public IEnumerable<string> SupportedInvestmentTypes => new[] { InvestmentType };

    private static readonly HashSet<string> KnownSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRAM GUMUS", "GRAM GÜMÜŞ", "GUMUS", "GÜMÜŞ", "SILVER", "XAG",
    };

    public SilverPriceProvider(HttpClient httpClient, IConfiguration configuration, IPriceHistoryStore historyStore)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _historyStore = historyStore;
        _httpClient.BaseAddress = new Uri("https://evds3.tcmb.gov.tr/igmevdsms-dis/");
    }

    private static void EnsureKnownSymbol(string symbol)
    {
        if (!KnownSymbols.Contains(symbol.Trim()))
            throw new ExternalServiceException(
                $"'{symbol}' tanımlı bir gümüş sembolü değil. Kullanılabilir: GRAM GÜMÜŞ.");
    }

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(
        string symbol, string investmentType, CancellationToken ct = default)
    {
        var to = DateTime.Today;
        // Hafta sonu ve resmi tatil bosluklarini atlamak icin geriden basla.
        var from = to.AddDays(-10);
        var bars = await GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);

        if (bars.Count == 0)
            throw new ExternalServiceException($"'{symbol}' için gümüş fiyatı bulunamadı.");

        var last = bars[^1];
        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = last.Close,
            // Fiyat gercek zamanli degil, son islem gununun kapanisi.
            AsOf = last.Date,
            LongName = "Gram Gümüş",
        };
    }

    /// <summary>
    /// Kullanıcı isteklerinin geçtiği yol: önce kendi veritabanımıza bakılır,
    /// EVDS'ye yalnızca depo istenen aralığı kapsamıyorsa gidilir.
    /// </summary>
    public Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        EnsureKnownSymbol(symbol);
        return HistoryBackfill.ReadWithBackfillAsync(
            _historyStore, this, symbol, InvestmentType, from, to, ct);
    }

    /// EVDS'ye giden gerçek istek — yalnızca depoda eksik olan aralık için
    /// ve gecelik senkron işinde çağrılır.
    public async Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default)
    {
        EnsureKnownSymbol(symbol);

        var apiKey = _configuration["MarketData:TcmbEvdsApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ExternalServiceException("TCMB EVDS API anahtarı yapılandırılmamış.");

        var jsonFieldName = SeriesCode.Replace('.', '_');

        // TCMB'nin API'si "?" ayracı kullanmıyor — path doğrudan parametrelerle devam ediyor.
        var url = $"series={SeriesCode}&startDate={from:dd-MM-yyyy}&endDate={to:dd-MM-yyyy}&type=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("key", apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("TCMB EVDS gümüş sorgusu başarısız oldu.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("items", out var itemsElement))
            throw new ExternalServiceException("TCMB EVDS'de gümüş verisi bulunamadı.");

        var bars = new List<PriceBarDto>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (!item.TryGetProperty(jsonFieldName, out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.String)
                continue; // islem gormeyen gunlerde deger bos donebilir

            var valueString = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(valueString) ||
                !decimal.TryParse(valueString, NumberStyles.Any, CultureInfo.InvariantCulture, out var perKilogram))
                continue;

            var perGram = perKilogram / GramsPerKilogram;

            // "Tarih" alanının biçimi seriye göre değişiyor — her seride tutarlı
            // olan UNIXTIME alanı daha güvenilir.
            var unixSeconds = long.Parse(item.GetProperty("UNIXTIME").GetProperty("$numberLong").GetString()!);
            var date = EvdsDate.FromUnixSeconds(unixSeconds);

            // BİST yalnızca kapanış yayınlıyor: OHLC'nin dördü de aynı,
            // hacim bilgisi yok.
            bars.Add(new PriceBarDto
            {
                Date = date,
                Open = perGram, High = perGram, Low = perGram, Close = perGram,
                Volume = 0,
            });
        }

        return bars.OrderBy(b => b.Date).ToList();
    }
}
