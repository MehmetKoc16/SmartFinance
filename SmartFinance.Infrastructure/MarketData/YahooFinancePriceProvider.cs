using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class YahooFinancePriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;

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
        var days = Math.Max(1, (to - from).Days);
        var range = days <= 30 ? "1mo" : days <= 90 ? "3mo" : days <= 180 ? "6mo" : "1y";

        var response = await _httpClient.GetAsync($"v8/finance/chart/{yahooSymbol}?range={range}&interval=1d", ct);
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

            var date = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.Date;
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

        return bars.Where(b => b.Date >= from.Date && b.Date <= to.Date).OrderBy(b => b.Date).ToList();
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
