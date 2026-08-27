using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class YahooFinancePriceProvider : IPriceProvider, IBatchPriceProvider, IBatchBarProvider, IHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IPriceHistoryStore _historyStore;

    // Yahoo'nun quote ucu tek istekte cok sembol kabul ediyor (26.08.2026'da
    // 10 sembolle dogrulandi). 50, guvenli bir ust sinir — URL uzunlugu ve
    // saglayici toleransi acisindan sorun cikarmayacak buyukluk.
    public int MaxBatchSize => 50;

    // quoteSummary uc noktasi (fiyat/grafik uc noktasinin aksine) bir oturum cerezi +
    // "crumb" token'i istiyor — tum saglayici ornekleri arasinda paylasilan static
    // alanlarda tutuluyor ki her istatistik istegi icin yeniden el sikisma yapilmasin.
    private static string? _cachedCrumb;
    private static readonly SemaphoreSlim _crumbLock = new(1, 1);

    public IEnumerable<string> SupportedInvestmentTypes => new[] { InvestmentType };

    // Depoda bu tiple saklanıyor; aynı kod farklı piyasalarda çakışabileceği için
    // tekillik sembol + tip üzerinden kuruluyor.
    private const string InvestmentType = "stock";

    // Yahoo'nun hız sınırı belgelenmemiş ve IP bazlı (resmi olmayan API), bu
    // yüzden senkron işinde semboller arasında ihtiyatlı bir ara bırakılıyor.
    public TimeSpan InterSymbolDelay => TimeSpan.FromSeconds(3);

    public YahooFinancePriceProvider(HttpClient httpClient, IPriceHistoryStore historyStore)
    {
        _httpClient = httpClient;
        _historyStore = historyStore;
        _httpClient.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
    }

    // Tüm "stock" tipi yatırımlar BIST hissesi kabul ediliyor
    private static string ToYahooSymbol(string symbol) =>
        symbol.Contains('.') ? symbol : $"{symbol}.IS";

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var yahooSymbol = ToYahooSymbol(symbol);
        var response = await _httpClient.GetAsync($"v8/finance/chart/{yahooSymbol}?range=5d&interval=1d", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"Yahoo Finance fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = GetFirstResultOrThrow(doc, symbol);
        var meta = result.GetProperty("meta");

        if (!meta.TryGetProperty("regularMarketPrice", out var priceElement))
            throw new ExternalServiceException($"Yahoo Finance'da '{symbol}' için güncel fiyat alınamadı.");

        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = priceElement.GetDecimal(),
            AsOf = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Kullanıcı isteklerinin geçtiği yol: günlük barlar önce kendi
    /// veritabanımızdan okunur, Yahoo'ya yalnızca depo istenen aralığı
    /// kapsamıyorsa gidilir. Böylece grafik istekleri kullanıcı sayısıyla
    /// birlikte büyüyen bir dış yük oluşturmuyor — Yahoo'nun sınırı
    /// belgelenmemiş ve IP bazlı olduğu için bu önemli.
    ///
    /// Gün içi (5 dakikalık) barlar istisna: saklanmıyor, doğrudan Yahoo'dan
    /// geliyor. Gün içi veri saatlerle ölçülür ve ertesi gün değersizdir;
    /// günlük barlarla aynı tabloda tutmak tekillik anahtarını da bozardı.
    /// Bu isteklerin dış yükünü mevcut önbellek sınırlıyor.
    /// </summary>
    public Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // from==to -> gun-ici (saatlik) istek: MarketDataService "1d" araligi icin bu sinyali gonderiyor.
        if (from.Date == to.Date)
            return FetchChartAsync(symbol, from, to, intraday: true, ct);

        return HistoryBackfill.ReadWithBackfillAsync(_historyStore, this, symbol, InvestmentType, from, to, ct);
    }

    /// <summary>
    /// Yahoo'ya giden gerçek istek — yalnızca ilk kez görülen sembollerde ve
    /// gecelik senkron işinde çağrılır.
    /// </summary>
    public Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default)
        => FetchChartAsync(symbol, from, to, intraday: false, ct);

    private async Task<IReadOnlyList<PriceBarDto>> FetchChartAsync(
        string symbol, DateTime from, DateTime to, bool intraday, CancellationToken ct)
    {
        var yahooSymbol = ToYahooSymbol(symbol);
        var isIntraday = intraday;
        var days = Math.Max(1, (to - from).Days);
        var yahooRange = isIntraday ? "1d"
            : days <= 7 ? "5d"
            : days <= 30 ? "1mo"
            : days <= 90 ? "3mo"
            : days <= 180 ? "6mo"
            : days <= 365 ? "1y"
            : "5y";
        var interval = isIntraday ? "5m" : "1d";

        var response = await _httpClient.GetAsync($"v8/finance/chart/{yahooSymbol}?range={yahooRange}&interval={interval}", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"Yahoo Finance geçmiş fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = GetFirstResultOrThrow(doc, symbol);

        if (!result.TryGetProperty("timestamp", out var timestamps))
            return Array.Empty<PriceBarDto>();

        var quote = result.GetProperty("indicators").GetProperty("quote")[0];
        var opens = quote.GetProperty("open");
        var highs = quote.GetProperty("high");
        var lows = quote.GetProperty("low");
        var closes = quote.GetProperty("close");
        var volumes = quote.GetProperty("volume");

        var bars = new List<PriceBarDto>();
        var count = timestamps.GetArrayLength();
        for (int i = 0; i < count; i++)
        {
            // Piyasanın kapalı olduğu günler için Yahoo null değer döner — bu barları atla
            if (closes[i].ValueKind == JsonValueKind.Null) continue;

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;
            // Gun-ici barlarda saat bilgisi korunur (grafikte saat bazli gosterim icin);
            // gunluk barlarda geceyarisina yuvarlanir (mevcut davranis).
            var date = isIntraday ? timestamp : timestamp.Date;
            bars.Add(new PriceBarDto
            {
                Date = date,
                Open = opens[i].ValueKind == JsonValueKind.Null ? closes[i].GetDecimal() : opens[i].GetDecimal(),
                High = highs[i].ValueKind == JsonValueKind.Null ? closes[i].GetDecimal() : highs[i].GetDecimal(),
                Low = lows[i].ValueKind == JsonValueKind.Null ? closes[i].GetDecimal() : lows[i].GetDecimal(),
                Close = closes[i].GetDecimal(),
                Volume = volumes[i].ValueKind == JsonValueKind.Null ? 0 : volumes[i].GetDecimal(),
            });
        }

        // Gun-ici barlarda "to.Date" geceyarisi anlamina geldigi icin ogleden sonraki
        // butun barlari yanlislikla elerdi — bu durumda Yahoo'nun range=1d yanitina guvenilir.
        if (isIntraday)
            return bars.OrderBy(b => b.Date).ToList();

        return bars.Where(b => b.Date >= from.Date && b.Date <= to.Date).OrderBy(b => b.Date).ToList();
    }

    // Fiyat geçmişinin aksine istatistikler tamamlayıcı bilgi — sorgu başarısız olursa
    // (sembolde eksik veri, Yahoo'nun bu modülü döndürmemesi vb.) tüm teknik analiz
    // yanıtını düşürmek yerine null dönüp devam ediyoruz.
    public async Task<StockStatisticsDto?> GetStatisticsAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var yahooSymbol = ToYahooSymbol(symbol);
            var response = await SendQuoteSummaryRequestAsync(yahooSymbol, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Crumb gecersizlesmis olabilir (oturum degismis) — bir kez yenileyip tekrar dene.
                _cachedCrumb = null;
                response = await SendQuoteSummaryRequestAsync(yahooSymbol, ct);
            }
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var results = doc.RootElement.GetProperty("quoteSummary").GetProperty("result");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;

            var result = results[0];
            var summaryDetail = result.TryGetProperty("summaryDetail", out var sd) ? sd : default;
            var keyStats = result.TryGetProperty("defaultKeyStatistics", out var ks) ? ks : default;
            var financialData = result.TryGetProperty("financialData", out var fd) ? fd : default;

            var marketCap = TryGetRaw(summaryDetail, "marketCap");
            var priceToBook = TryGetRaw(keyStats, "priceToBook");

            return new StockStatisticsDto
            {
                Open = TryGetRaw(summaryDetail, "open"),
                PreviousClose = TryGetRaw(summaryDetail, "previousClose"),
                DayHigh = TryGetRaw(summaryDetail, "dayHigh"),
                DayLow = TryGetRaw(summaryDetail, "dayLow"),
                FiftyTwoWeekHigh = TryGetRaw(summaryDetail, "fiftyTwoWeekHigh"),
                FiftyTwoWeekLow = TryGetRaw(summaryDetail, "fiftyTwoWeekLow"),
                AverageVolume = TryGetRaw(summaryDetail, "averageVolume"),
                MarketCap = marketCap,
                TrailingPE = TryGetRaw(summaryDetail, "trailingPE"),
                PriceToBook = priceToBook,
                EquityValue = (marketCap.HasValue && priceToBook is > 0) ? marketCap / priceToBook : null,
                ReturnOnEquity = TryGetRaw(financialData, "returnOnEquity"),
                Ebitda = TryGetRaw(financialData, "ebitda"),
                ProfitMargin = TryGetRaw(financialData, "profitMargins"),
                GrossMargin = TryGetRaw(financialData, "grossMargins"),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Birden fazla sembolün fiyatını tek istekte alır (v7/finance/quote).
    /// Arka plandaki fiyat yenileyici bunu kullanır: 100 sembol, 50'şerlik
    /// iki istekte gelir — sembol başına ayrı istek atmak yerine.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var bars = await GetTodayBarsAsync(symbols, ct);
        return bars.ToDictionary(kv => kv.Key, kv => kv.Value.Close, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Aynı toplu istekten günün tam barını çıkarır. Yahoo'nun quote yanıtı
    /// açılış/gün içi en yüksek-en düşük/hacim alanlarını zaten içerdiği için
    /// bu bilgi bedava geliyor; arka plan yenileyici bunu depoya yazarak
    /// seans sürerken grafiğin son mumunun eksik kalmasını önlüyor.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PriceBarDto>> GetTodayBarsAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PriceBarDto>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return result;

        // Yahoo sembolü ("THYAO" -> "THYAO.IS") ile bizim sembolümüz arasında
        // geri eşleme gerekiyor: yanıt Yahoo biçiminde geliyor.
        var byYahooSymbol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
            byYahooSymbol[ToYahooSymbol(s)] = s.Trim().ToUpperInvariant();

        var joined = string.Join(",", byYahooSymbol.Keys);
        var response = await SendQuoteRequestAsync(joined, ct);

        // Crumb süresi dolmuş olabilir — bir kez yenileyip tekrar dene.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _cachedCrumb = null;
            response = await SendQuoteRequestAsync(joined, ct);
        }

        if (!response.IsSuccessStatusCode) return result;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("quoteResponse", out var qr) ||
            !qr.TryGetProperty("result", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("symbol", out var symEl)) continue;
            if (!item.TryGetProperty("regularMarketPrice", out var priceEl)) continue;
            if (priceEl.ValueKind != JsonValueKind.Number) continue;

            var yahooSymbol = symEl.GetString();
            if (yahooSymbol == null) continue;
            if (!byYahooSymbol.TryGetValue(yahooSymbol, out var original)) continue;

            var close = priceEl.GetDecimal();

            // Gün içi alanlar eksik gelebiliyor (işlem görmeyen sembol, veri
            // gecikmesi); o durumda barın tamamı son fiyata düşürülür — yanlış
            // bir sıfır değeri grafikte uçuk bir mum olarak görünürdü.
            result[original] = new PriceBarDto
            {
                Date = TryGetNumber(item, "regularMarketTime") is { } unix
                    ? DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime.Date
                    : DateTime.UtcNow.Date,
                Open = TryGetNumber(item, "regularMarketOpen") ?? close,
                High = TryGetNumber(item, "regularMarketDayHigh") ?? close,
                Low = TryGetNumber(item, "regularMarketDayLow") ?? close,
                Close = close,
                Volume = TryGetNumber(item, "regularMarketVolume") ?? 0,
            };
        }

        return result;
    }

    private static decimal? TryGetNumber(JsonElement element, string fieldName)
    {
        if (!element.TryGetProperty(fieldName, out var field)) return null;
        if (field.ValueKind != JsonValueKind.Number) return null;
        return field.GetDecimal();
    }

    private async Task<HttpResponseMessage> SendQuoteRequestAsync(string joinedSymbols, CancellationToken ct)
    {
        await EnsureCrumbAsync(ct);
        var url = $"v7/finance/quote?symbols={Uri.EscapeDataString(joinedSymbols)}";
        if (_cachedCrumb != null)
            url += $"&crumb={Uri.EscapeDataString(_cachedCrumb)}";
        return await _httpClient.GetAsync(url, ct);
    }

    private async Task<HttpResponseMessage> SendQuoteSummaryRequestAsync(string yahooSymbol, CancellationToken ct)
    {
        await EnsureCrumbAsync(ct);
        var url = $"v10/finance/quoteSummary/{yahooSymbol}?modules=summaryDetail,defaultKeyStatistics,financialData";
        if (_cachedCrumb != null)
            url += $"&crumb={Uri.EscapeDataString(_cachedCrumb)}";
        return await _httpClient.GetAsync(url, ct);
    }

    // fc.yahoo.com'a yapilan istek 404 dondurse bile oturum cerezini set ediyor —
    // bu cerezle query1.finance.yahoo.com/v1/test/getcrumb'dan gecerli bir crumb alinabiliyor.
    private async Task EnsureCrumbAsync(CancellationToken ct)
    {
        if (_cachedCrumb != null) return;
        await _crumbLock.WaitAsync(ct);
        try
        {
            if (_cachedCrumb != null) return;
            await _httpClient.GetAsync("https://fc.yahoo.com", ct);
            var response = await _httpClient.GetAsync("https://query1.finance.yahoo.com/v1/test/getcrumb", ct);
            if (response.IsSuccessStatusCode)
                _cachedCrumb = await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            _crumbLock.Release();
        }
    }

    // Yahoo'nun sayisal alanlari genelde {"raw": 123.45, "fmt": "123.45"} seklinde gelir.
    private static decimal? TryGetRaw(JsonElement module, string fieldName)
    {
        if (module.ValueKind != JsonValueKind.Object) return null;
        if (!module.TryGetProperty(fieldName, out var field)) return null;
        if (field.ValueKind != JsonValueKind.Object) return null;
        if (!field.TryGetProperty("raw", out var raw)) return null;
        if (raw.ValueKind != JsonValueKind.Number) return null;
        return raw.GetDecimal();
    }

    private static JsonElement GetFirstResultOrThrow(JsonDocument doc, string symbol)
    {
        var chart = doc.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            throw new ExternalServiceException($"Yahoo Finance'da '{symbol}' sembolü bulunamadı.");

        var results = chart.GetProperty("result");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new ExternalServiceException($"Yahoo Finance'da '{symbol}' sembolü bulunamadı.");

        return results[0];
    }
}
