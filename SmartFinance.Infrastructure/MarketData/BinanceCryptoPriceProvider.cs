using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

/// <summary>
/// Kripto fiyatları — Binance. CoinGecko yerine geçti (29.08.2026).
///
/// Neden değişti:
/// - Hız sınırı: CoinGecko Demo anahtarıyla dakikada 100 istek, Binance
///   anahtarsız dakikada 6000 birim (~60 kat).
/// - Fiyat doğrudan TL: Binance'te 308 TRY paritesi var, USD çekip çevirmeye
///   gerek kalmıyor. Çeviri adımı kur kaynağına bağımlılık ve hata payıydı.
/// - GERÇEK OHLC: CoinGecko'nun market_chart ucu yalnızca fiyat noktası
///   veriyordu, mum grafiğinin dört alanına da aynı değer yazılıyordu.
///
/// Sembol çözümleme sırası: doğrudan TRY paritesi -> USDT paritesi x USDTTRY
/// -> CoinGecko. Böylece Binance'te hiç listelenmeyen coin'ler (örn. TON)
/// için kapsama kaybı olmuyor.
/// </summary>
public class BinanceCryptoPriceProvider : IPriceProvider, IBatchPriceProvider, IBatchBarProvider, IHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IPriceHistoryStore _historyStore;
    private readonly IMemoryCache _cache;
    private readonly CoinGeckoPriceProvider _fallback;

    private const string InvestmentType = "crypto";
    private const string StableQuote = "USDT";
    private const string TryQuote = "TRY";
    private const string StablePair = "USDTTRY";

    // ticker/tradingDay ağırlığı sembol başına 4, üst sınır 200. 50 sembol =
    // 200 ağırlık; dakikalık 6000 sınırının çok altında.
    public int MaxBatchSize => 50;

    public TimeSpan InterSymbolDelay => TimeSpan.FromSeconds(1);

    public IEnumerable<string> SupportedInvestmentTypes => new[] { InvestmentType };

    // Yeniden adlandırılan coin'ler. Kullanıcı eski kodu yazmış olabilir;
    // Binance yalnızca yeni kodu listeliyor.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "MATIC", "POL" },     // Polygon 2024'te POL'e geçti
        { "RNDR", "RENDER" },   // Render Network yeniden adlandırıldı
    };

    private const string SymbolCacheKey = "binance:symbols";
    private static readonly TimeSpan SymbolCacheTtl = TimeSpan.FromHours(12);

    public BinanceCryptoPriceProvider(
        HttpClient httpClient,
        IPriceHistoryStore historyStore,
        IMemoryCache cache,
        CoinGeckoPriceProvider fallback)
    {
        _httpClient = httpClient;
        _historyStore = historyStore;
        _cache = cache;
        _fallback = fallback;
        _httpClient.BaseAddress = new Uri("https://api.binance.com/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
    }

    // ─── Sembol çözümleme ────────────────────────────────────────

    /// Bir sembolün Binance'te nasıl fiyatlanacağı.
    /// Direct: doğrudan TRY paritesi. Composite: USDT paritesi x USDTTRY.
    private sealed record Resolved(string Pair, bool NeedsUsdtConversion);

    private static string Normalize(string symbol)
    {
        var s = symbol.Trim().ToUpperInvariant();
        return Aliases.TryGetValue(s, out var yeni) ? yeni : s;
    }

    private async Task<Resolved?> ResolveAsync(string symbol, CancellationToken ct)
    {
        var s = Normalize(symbol);
        var known = await GetTradingSymbolsAsync(ct);

        if (known.Contains(s + TryQuote)) return new Resolved(s + TryQuote, false);
        if (known.Contains(s + StableQuote)) return new Resolved(s + StableQuote, true);
        return null;
    }

    /// <summary>
    /// Binance'te işlem gören tüm parite adları. Yanıt büyük olduğu için
    /// günde iki kez çekilip önbellekte tutuluyor — sembol başına ayrı
    /// "var mı" sorgusu atmak yerine.
    /// </summary>
    private async Task<HashSet<string>> GetTradingSymbolsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(SymbolCacheKey, out HashSet<string>? cached) && cached is not null)
            return cached;

        var response = await _httpClient.GetAsync("api/v3/exchangeInfo?permissions=SPOT", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("Binance sembol listesi alınamadı.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("symbols", out var symbols))
        {
            foreach (var s in symbols.EnumerateArray())
            {
                if (s.TryGetProperty("status", out var st) && st.GetString() != "TRADING") continue;
                if (s.TryGetProperty("symbol", out var sym) && sym.GetString() is { } name)
                    set.Add(name);
            }
        }

        if (set.Count == 0)
            throw new ExternalServiceException("Binance sembol listesi boş döndü.");

        _cache.Set(SymbolCacheKey, set, SymbolCacheTtl);
        return set;
    }

    // ─── IPriceProvider ──────────────────────────────────────────

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(
        string symbol, string investmentType, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(symbol, ct);
        if (resolved is null)
            return await _fallback.GetCurrentPriceAsync(symbol, investmentType, ct);

        var bar = await FetchTodayBarAsync(resolved, ct);
        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = bar.Close,
            AsOf = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Kullanıcı isteklerinin geçtiği yol: günlük barlar önce kendi
    /// veritabanımızdan okunur, Binance'e yalnızca depo istenen aralığı
    /// kapsamıyorsa gidilir. Gün içi (5 dakikalık) barlar saklanmaz.
    /// </summary>
    public async Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(symbol, ct);
        if (resolved is null)
            return await _fallback.GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);

        // from==to -> gun-ici istek: MarketDataService "1d" araligi icin bu sinyali
        // gonderiyor. Kripto 7/24 islem gordugu icin "son 24 saat" dogru karsiligi.
        if (from.Date == to.Date)
            return await FetchBarsAsync(resolved, "5m", startTime: null, limit: 288, ct);

        return await HistoryBackfill.ReadWithBackfillAsync(
            _historyStore, this, symbol, InvestmentType, from, to, ct);
    }

    // ─── IHistorySource ──────────────────────────────────────────

    public async Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(symbol, ct);
        if (resolved is null) return Array.Empty<PriceBarDto>();

        var startMs = new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        // Binance tek istekte en fazla 1000 bar döndürüyor (~3 yıl).
        return await FetchBarsAsync(resolved, "1d", startMs, limit: 1000, ct);
    }

    // ─── IBatchPriceProvider / IBatchBarProvider ─────────────────

    public async Task<IReadOnlyDictionary<string, decimal>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var bars = await GetTodayBarsAsync(symbols, ct);
        return bars.ToDictionary(kv => kv.Key, kv => kv.Value.Close, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Günün barını TEK istekte, tüm semboller için alır (ticker/tradingDay).
    /// Arka plandaki yenileyici bunu hem önbelleğe hem geçmiş deposuna yazıyor;
    /// böylece gün ilerlerken grafiğin son mumu eksik kalmıyor.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PriceBarDto>> GetTodayBarsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PriceBarDto>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return result;

        var resolvedBySymbol = new Dictionary<string, Resolved>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            var r = await ResolveAsync(s, ct);
            // Binance'te olmayanlar burada atlanır; fiyatları istek anında
            // CoinGecko'dan geliyor, toplu yenilemeye dahil değiller.
            if (r is not null) resolvedBySymbol[s.Trim().ToUpperInvariant()] = r;
        }
        if (resolvedBySymbol.Count == 0) return result;

        var pairs = resolvedBySymbol.Values.Select(r => r.Pair).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (resolvedBySymbol.Values.Any(r => r.NeedsUsdtConversion))
            pairs.Add(StablePair);

        var tickers = await FetchTradingDayAsync(pairs, ct);
        if (!tickers.TryGetValue(StablePair, out var usdtTry)) usdtTry = null;

        var today = DateTime.UtcNow.Date;
        foreach (var (symbol, resolved) in resolvedBySymbol)
        {
            if (!tickers.TryGetValue(resolved.Pair, out var t)) continue;

            var bar = t.ToBar(today);
            if (resolved.NeedsUsdtConversion)
            {
                if (usdtTry is null) continue;
                bar = Multiply(bar, usdtTry.ToBar(today));
            }
            result[symbol] = bar;
        }

        return result;
    }

    // ─── Binance çağrıları ───────────────────────────────────────

    private sealed record Ticker(decimal Open, decimal High, decimal Low, decimal Last, decimal Volume)
    {
        public PriceBarDto ToBar(DateTime date) => new()
        {
            Date = date, Open = Open, High = High, Low = Low, Close = Last, Volume = Volume,
        };
    }

    private async Task<Dictionary<string, Ticker>> FetchTradingDayAsync(
        IReadOnlyCollection<string> pairs, CancellationToken ct)
    {
        var list = "[" + string.Join(",", pairs.Select(p => $"\"{p}\"")) + "]";
        var url = $"api/v3/ticker/tradingDay?symbols={Uri.EscapeDataString(list)}";

        var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("Binance kripto fiyatı sorgusu başarısız oldu.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = new Dictionary<string, Ticker>(StringComparer.OrdinalIgnoreCase);
        // Tek sembol istendiğinde Binance dizi yerine tek nesne döndürüyor.
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        foreach (var t in items)
        {
            if (!t.TryGetProperty("symbol", out var symEl) || symEl.GetString() is not { } sym) continue;
            result[sym] = new Ticker(
                ParseDecimal(t.GetProperty("openPrice")),
                ParseDecimal(t.GetProperty("highPrice")),
                ParseDecimal(t.GetProperty("lowPrice")),
                ParseDecimal(t.GetProperty("lastPrice")),
                ParseDecimal(t.GetProperty("volume")));
        }

        return result;
    }

    private async Task<PriceBarDto> FetchTodayBarAsync(Resolved resolved, CancellationToken ct)
    {
        var bars = await FetchBarsAsync(resolved, "1d", startTime: null, limit: 1, ct);
        if (bars.Count == 0)
            throw new ExternalServiceException($"Binance'te '{resolved.Pair}' için fiyat bulunamadı.");
        return bars[^1];
    }

    /// <summary>
    /// Kline (mum) verisi. Sembolün doğrudan TRY paritesi yoksa USDT paritesi
    /// çekilip aynı günün USDTTRY barıyla çarpılır.
    /// </summary>
    /// Not: çarpım OHLC için yaklaşıktır — iki serinin en yüksekleri aynı ana
    /// denk gelmeyebilir. Kapanış değeri kesindir, gün içi uçlarda birkaç
    /// baz puanlık sapma olabilir. Doğrudan TRY paritesi olan sembollerde
    /// (yaygın coin'lerin tamamı) bu durum yaşanmaz.
    private async Task<IReadOnlyList<PriceBarDto>> FetchBarsAsync(
        Resolved resolved, string interval, long? startTime, int limit, CancellationToken ct)
    {
        var bars = await FetchKlinesAsync(resolved.Pair, interval, startTime, limit, ct);
        if (!resolved.NeedsUsdtConversion) return bars;

        var fx = await FetchKlinesAsync(StablePair, interval, startTime, limit, ct);
        var fxByDate = fx.ToDictionary(b => b.Date);

        var converted = new List<PriceBarDto>();
        foreach (var b in bars)
        {
            if (!fxByDate.TryGetValue(b.Date, out var kur)) continue;
            converted.Add(Multiply(b, kur));
        }
        return converted;
    }

    private static PriceBarDto Multiply(PriceBarDto bar, PriceBarDto fx) => new()
    {
        Date = bar.Date,
        Open = bar.Open * fx.Open,
        High = bar.High * fx.High,
        Low = bar.Low * fx.Low,
        Close = bar.Close * fx.Close,
        // Hacim coin adedi; kur çarpımı uygulanmaz.
        Volume = bar.Volume,
    };

    private async Task<IReadOnlyList<PriceBarDto>> FetchKlinesAsync(
        string pair, string interval, long? startTime, int limit, CancellationToken ct)
    {
        var url = $"api/v3/klines?symbol={pair}&interval={interval}&limit={limit}";
        if (startTime.HasValue) url += $"&startTime={startTime.Value}";

        var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"Binance geçmiş fiyat sorgusu başarısız oldu: {pair}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ExternalServiceException($"Binance yanıtı beklenen biçimde değil: {pair}");

        var bars = new List<PriceBarDto>();
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            // Binance kline dizisi: [acilisZamani, acilis, yuksek, dusuk, kapanis, hacim, ...]
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() < 6) continue;

            var date = DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime;
            bars.Add(new PriceBarDto
            {
                Date = interval == "1d" ? date.Date : date,
                Open = ParseDecimal(k[1]),
                High = ParseDecimal(k[2]),
                Low = ParseDecimal(k[3]),
                Close = ParseDecimal(k[4]),
                Volume = ParseDecimal(k[5]),
            });
        }

        return bars.OrderBy(b => b.Date).ToList();
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        // Binance sayısal alanları string olarak döndürüyor ("3772250.00000000").
        var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        if (string.IsNullOrWhiteSpace(raw) ||
            !decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            throw new ExternalServiceException("Binance fiyatı ayrıştırılamadı.");
        return value;
    }
}
