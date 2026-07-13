using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class TefasPriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "fund" };

    public TefasPriceProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://www.tefas.gov.tr/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Referer"))
        {
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.tefas.gov.tr/TarihselVeriler.aspx");
        }
    }

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-10); // hafta sonu/tatil boşluklarını atlamak için birkaç gün geriden başla
        var bars = await GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);

        if (bars.Count == 0)
            throw new ExternalServiceException($"TEFAS'ta '{symbol}' fon kodu için fiyat bulunamadı.");

        var last = bars[^1];
        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = last.Close,
            AsOf = last.Date,
        };
    }

    public async Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var formData = new Dictionary<string, string>
        {
            ["fontip"] = "YAT",
            ["fonkod"] = symbol.ToUpperInvariant(),
            ["bastarih"] = from.ToString("dd.MM.yyyy"),
            ["bittarih"] = to.ToString("dd.MM.yyyy"),
        };

        using var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient.PostAsync("api/DB/BindHistoryInfo", content, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TEFAS geçmiş fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.GetArrayLength() == 0)
            throw new ExternalServiceException($"TEFAS'ta '{symbol}' fon kodu bulunamadı.");

        var bars = new List<PriceBarDto>();
        foreach (var item in dataElement.EnumerateArray())
        {
            var date = ParseTefasDate(item.GetProperty("TARIH").GetString()!);
            var price = item.GetProperty("FIYAT").GetDecimal();

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

        return bars.OrderBy(b => b.Date).ToList();
    }

    private static DateTime ParseTefasDate(string aspNetDate)
    {
        // Format: "/Date(1700000000000)/"
        var start = aspNetDate.IndexOf('(') + 1;
        var end = aspNetDate.IndexOf(')');
        var millis = long.Parse(aspNetDate[start..end]);
        return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.Date;
    }
}
