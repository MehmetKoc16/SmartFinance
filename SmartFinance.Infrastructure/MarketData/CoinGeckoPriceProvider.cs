using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class CoinGeckoPriceProvider : IPriceProvider, IBatchPriceProvider
{
    private readonly HttpClient _httpClient;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "crypto" };

    public CoinGeckoPriceProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.coingecko.com/api/v3/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            // Cloudflare, User-Agent'sız istekleri bot sayıp 403 döndürüyor
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        // Anahtarsız (public) katman dakikada yalnızca 5-15 istek veriyor —
        // birkaç farklı kripto tutan tek bir kullanıcının portföy yenilemesi
        // bile bunu aşabiliyor. Ücretsiz Demo hesabı anahtarı sınırı dakikada
        // 100'e çıkarıyor. Anahtar tanımlı değilse uygulama yine çalışır,
        // yalnızca dar sınırla — bu yüzden zorunlu tutulmuyor.
        var apiKey = configuration["MarketData:CoinGeckoApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("x-cg-demo-api-key"))
        {
            _httpClient.DefaultRequestHeaders.Add("x-cg-demo-api-key", apiKey);
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

    // simple/price ucu virgulle ayrilmis coin id listesi kabul ediyor.
    // 50, URL uzunlugu acisindan guvenli bir ust sinir.
    public int MaxBatchSize => 50;

    /// <summary>
    /// Birden fazla kripto paranın fiyatını tek istekte alır.
    /// Sembol -> coin id çözümlemesi çoğunlukla yerel haritadan (CryptoSymbolMap)
    /// yapıldığı için ek istek gerektirmez; haritada olmayan semboller
    /// çözümlenemezse sonuçtan sessizce düşer.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return result;

        // coin id -> bizim sembolumuz (yanit id bazli geliyor)
        var bySymbolId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            try
            {
                var id = await ResolveCoinIdAsync(s.Trim().ToUpperInvariant(), ct);
                bySymbolId[id] = s.Trim().ToUpperInvariant();
            }
            catch
            {
                // Cozumlenemeyen sembol toplu istegi bozmamali.
            }
        }

        if (bySymbolId.Count == 0) return result;

        var ids = string.Join(",", bySymbolId.Keys);
        var response = await _httpClient.GetAsync(
            $"simple/price?ids={Uri.EscapeDataString(ids)}&vs_currencies=try", ct);
        if (!response.IsSuccessStatusCode) return result;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var (coinId, originalSymbol) in bySymbolId)
        {
            if (!doc.RootElement.TryGetProperty(coinId, out var coinEl)) continue;
            if (!coinEl.TryGetProperty("try", out var priceEl)) continue;
            if (priceEl.ValueKind != JsonValueKind.Number) continue;

            result[originalSymbol] = priceEl.GetDecimal();
        }

        return result;
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
