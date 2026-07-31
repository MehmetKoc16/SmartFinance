using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class YahooFinancePriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;

    // quoteSummary uc noktasi (fiyat/grafik uc noktasinin aksine) bir oturum cerezi +
    // "crumb" token'i istiyor — tum saglayici ornekleri arasinda paylasilan static
    // alanlarda tutuluyor ki her istatistik istegi icin yeniden el sikisma yapilmasin.
    private static string? _cachedCrumb;
    private static readonly SemaphoreSlim _crumbLock = new(1, 1);

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "stock" };

    public YahooFinancePriceProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
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

    public async Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var yahooSymbol = ToYahooSymbol(symbol);
        // from==to -> gun-ici (saatlik) istek: MarketDataService "1d" araligi icin bu sinyali gonderiyor.
        var isIntraday = from.Date == to.Date;
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
