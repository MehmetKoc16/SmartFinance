using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class CoinGeckoPriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "crypto" };

    public CoinGeckoPriceProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            // Cloudflare, User-Agent'sız istekleri bot sayıp 403 döndürüyor
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
    }

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var coinId = await ResolveCoinIdAsync(symbol, ct);

        var response = await _httpClient.GetAsync($"simple/price?ids={coinId}&vs_currencies=try", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"CoinGecko fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty(coinId, out var coinElement) ||
            !coinElement.TryGetProperty("try", out var priceElement))
            throw new ExternalServiceException($"CoinGecko'da '{symbol}' için fiyat bulunamadı.");

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
        var coinId = await ResolveCoinIdAsync(symbol, ct);

        // from==to -> gun-ici (saatlik) istek: MarketDataService "1d" araligi icin bu sinyali gonderiyor.
        var isIntraday = from.Date == to.Date;
        var fromUnix = ((DateTimeOffset)DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var toUnix = ((DateTimeOffset)DateTime.SpecifyKind(isIntraday ? to.AddDays(1) : to, DateTimeKind.Utc)).ToUnixTimeSeconds();

        var response = await _httpClient.GetAsync(
            $"coins/{coinId}/market_chart/range?vs_currency=try&from={fromUnix}&to={toUnix}", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"CoinGecko geçmiş fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var bars = new List<PriceBarDto>();
        if (doc.RootElement.TryGetProperty("prices", out var pricesElement))
        {
            foreach (var point in pricesElement.EnumerateArray())
            {
                var timestampMs = point[0].GetInt64();
                var price = point[1].GetDecimal();
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
                // Gun-ici barlarda saat bilgisi korunur (grafikte saat bazli gosterim icin);
                // gunluk barlarda geceyarisina yuvarlanir (mevcut davranis).
                var date = isIntraday ? timestamp : timestamp.Date;

                bars.Add(new PriceBarDto
                {
                    Date = date,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price,
                    Volume = 0,
                });
            }
        }

        if (isIntraday)
            return bars.OrderBy(b => b.Date).ToList();

        // Uzun araliklarda CoinGecko saatlik veri donebilir — gunun son fiyatina indirgeyip gunluk bar olustur
        return bars
            .GroupBy(b => b.Date)
            .Select(g => g.Last())
            .OrderBy(b => b.Date)
            .ToList();
    }

    private async Task<string> ResolveCoinIdAsync(string symbol, CancellationToken ct)
    {
        if (CryptoSymbolMap.TickerToCoinGeckoId.TryGetValue(symbol, out var knownId))
            return knownId;

        var response = await _httpClient.GetAsync($"search?query={Uri.EscapeDataString(symbol)}", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"CoinGecko'da '{symbol}' sembolü aranamadı.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("coins", out var coinsElement) || coinsElement.GetArrayLength() == 0)
            throw new ExternalServiceException($"CoinGecko'da '{symbol}' sembolü bulunamadı.");

        var firstMatch = coinsElement.EnumerateArray()
            .FirstOrDefault(c => string.Equals(c.GetProperty("symbol").GetString(), symbol, StringComparison.OrdinalIgnoreCase));

        if (firstMatch.ValueKind == JsonValueKind.Undefined)
            firstMatch = coinsElement[0];

        return firstMatch.GetProperty("id").GetString()!;
    }
}
