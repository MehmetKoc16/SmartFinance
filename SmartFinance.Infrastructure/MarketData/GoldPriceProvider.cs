using System.Globalization;
using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Gram altın fiyatı — Binance'teki PAXG/TRY paritesinden türetiliyor.
///
/// PAXG (PAX Gold), Londra kasalarında saklanan fiziksel altınla 1 token = 1 ons
/// olarak teminatlandırılmış bir token. Gram fiyatı = PAXGTRY / 31,1034768.
///
/// Neden bu kaynak (29.08.2026'da ölçülerek seçildi):
/// - Önceki güncel fiyat kaynağı (GenelPara) sunucumuza Cloudflare bot koruması
///   döndürmeye başlamıştı; altın eklemek 502 ile tamamen kırılmıştı.
/// - Önceki geçmiş kaynağı (TCMB EVDS TP.MK.KUL.YTL) AYLIK bir seri ve o tarihte
///   en yeni verisi Mayıs 2026'ydı — 6 aylık grafik 3 noktaya düşüyor, RSI(14) ve
///   MACD(26) matematiksel olarak hesaplanamıyordu.
/// - Binance anahtarsız, dakikada 6000 birimlik sınırla çalışıyor ve GERÇEK OHLC
///   veriyor; diğer tüm kaynaklarımız yalnızca kapanış fiyatı veriyordu.
///
/// Doğruluk: 28.08.2026 kapanışı ₺6.915,66 hesaplandı, piyasa ~₺6.908 (%0,11 fark).
/// Sapmanın nedeni Türkiye'deki fiziksel gram altının uluslararası pariteye göre
/// taşıdığı primdir; kuyumcu etiketiyle kuruşu kuruşuna tutması beklenmez.
/// </summary>
public class GoldPriceProvider : IPriceProvider, IBatchPriceProvider, IBatchBarProvider, IHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IPriceHistoryStore _historyStore;

    private const string InvestmentType = "gold";

    // 1 troy ons = 31,1034768 gram (uluslararası standart).
    private const decimal GramsPerTroyOunce = 31.1034768m;

    private const string Pair = "PAXGTRY";

    // Binance'te yalnızca tek bir altın paritesi kullanılıyor, bu yüzden toplu
    // istek kavramı burada tek isteğe iniyor.
    public int MaxBatchSize => 50;

    // Binance dakikada 6000 birim veriyor (klines = 2 birim). Sınır cömert
    // olduğu için kısa bir ara yeterli.
    public TimeSpan InterSymbolDelay => TimeSpan.FromSeconds(1);

    public IEnumerable<string> SupportedInvestmentTypes => new[] { InvestmentType };

    // Kullanıcının yazabileceği farklı adlar aynı pariteye bağlanıyor.
    private static readonly HashSet<string> KnownSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "GRAM ALTIN", "ALTIN", "GOLD", "XAU", "PAXG",
    };

    public GoldPriceProvider(HttpClient httpClient, IPriceHistoryStore historyStore)
    {
        _httpClient = httpClient;
        _historyStore = historyStore;
        _httpClient.BaseAddress = new Uri("https://api.binance.com/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
    }

    private static void EnsureKnownSymbol(string symbol)
    {
        if (!KnownSymbols.Contains(symbol.Trim()))
            throw new ExternalServiceException(
                $"'{symbol}' tanımlı bir altın sembolü değil. Kullanılabilir: GRAM ALTIN.");
    }

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(
        string symbol, string investmentType, CancellationToken ct = default)
    {
        EnsureKnownSymbol(symbol);
        var bar = await FetchTodayBarAsync(ct);

        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = bar.Close,
            AsOf = DateTime.UtcNow,
            LongName = "Gram Altın",
        };
    }

    /// <summary>
    /// Kullanıcı isteklerinin geçtiği yol: günlük barlar önce kendi
    /// veritabanımızdan okunur, Binance'e yalnızca depo istenen aralığı
    /// kapsamıyorsa gidilir.
    ///
    /// Gün içi (5 dakikalık) barlar istisna: saklanmıyor, doğrudan çekiliyor —
    /// ertesi gün değersiz oldukları için günlük barlarla aynı tabloda
    /// tutmanın anlamı yok.
    /// </summary>
    public Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        EnsureKnownSymbol(symbol);

        // from==to -> gun-ici istek: MarketDataService "1d" araligi icin bu sinyali
        // gonderiyor. Altin 7/24 islem gordugu icin "son 24 saat" dogru karsiligi.
        if (from.Date == to.Date)
            return FetchKlinesAsync("5m", startTime: null, limit: 288, ct);

        return HistoryBackfill.ReadWithBackfillAsync(
            _historyStore, this, symbol, InvestmentType, from, to, ct);
    }

    /// Binance'e giden gerçek istek — yalnızca depoda eksik olan aralık için
    /// ve gecelik senkron işinde çağrılır.
    public Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default)
    {
        EnsureKnownSymbol(symbol);
        var startMs = new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        // Binance tek istekte en fazla 1000 bar döndürüyor; ~3 yıla denk geliyor.
        return FetchKlinesAsync("1d", startMs, limit: 1000, ct);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var bars = await GetTodayBarsAsync(symbols, ct);
        return bars.ToDictionary(kv => kv.Key, kv => kv.Value.Close, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Günün (kısmi) barı. Arka plandaki fiyat yenileyici bunu hem önbelleğe
    /// hem geçmiş deposuna yazıyor; böylece gün ilerlerken grafiğin son mumu
    /// eksik kalmıyor ve bunun için ek bir dış istek atılmıyor.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PriceBarDto>> GetTodayBarsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PriceBarDto>(StringComparer.OrdinalIgnoreCase);
        var wanted = symbols.Where(s => KnownSymbols.Contains(s.Trim())).ToList();
        if (wanted.Count == 0) return result;

        // Tüm altın sembolleri aynı pariteye bağlı — tek istek hepsine yetiyor.
        var bar = await FetchTodayBarAsync(ct);
        foreach (var s in wanted)
            result[s.Trim().ToUpperInvariant()] = bar;

        return result;
    }

    private async Task<PriceBarDto> FetchTodayBarAsync(CancellationToken ct)
    {
        var bars = await FetchKlinesAsync("1d", startTime: null, limit: 1, ct);
        if (bars.Count == 0)
            throw new ExternalServiceException("Binance'te gram altın fiyatı bulunamadı.");
        return bars[^1];
    }

    /// <summary>
    /// Binance kline (mum) verisini çeker ve ons cinsinden gelen değerleri
    /// grama çevirir.
    /// </summary>
    private async Task<IReadOnlyList<PriceBarDto>> FetchKlinesAsync(
        string interval, long? startTime, int limit, CancellationToken ct)
    {
        var url = $"api/v3/klines?symbol={Pair}&interval={interval}&limit={limit}";
        if (startTime.HasValue) url += $"&startTime={startTime.Value}";

        var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("Binance altın fiyatı sorgusu başarısız oldu.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ExternalServiceException("Binance altın yanıtı beklenen biçimde değil.");

        var bars = new List<PriceBarDto>();
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            // Binance kline dizisi: [acilisZamani, acilis, yuksek, dusuk, kapanis, hacim, ...]
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() < 6) continue;

            var openTimeMs = k[0].GetInt64();
            var date = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime;

            bars.Add(new PriceBarDto
            {
                // Gun-ici barlarda saat korunur, gunluk barlarda geceyarisina denk gelir.
                Date = interval == "1d" ? date.Date : date,
                Open = ToGram(k[1]),
                High = ToGram(k[2]),
                Low = ToGram(k[3]),
                Close = ToGram(k[4]),
                // Hacim PAXG (ons) cinsinden geliyor; fiyat grama çevrildiği için
                // hacim de grama çevriliyor ki fiyat x hacim = TL cirosu tutarlı kalsın.
                Volume = ParseDecimal(k[5]) * GramsPerTroyOunce,
            });
        }

        return bars.OrderBy(b => b.Date).ToList();
    }

    private static decimal ToGram(JsonElement element) => ParseDecimal(element) / GramsPerTroyOunce;

    private static decimal ParseDecimal(JsonElement element)
    {
        // Binance sayısal alanları string olarak döndürüyor ("4462.47000000").
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrWhiteSpace(raw) ||
            !decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            throw new ExternalServiceException("Binance altın fiyatı ayrıştırılamadı.");
        return value;
    }
}
