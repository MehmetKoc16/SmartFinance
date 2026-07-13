using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class TcmbPriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "currency", "gold" };

    public TcmbPriceProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        // 2024 sonrası EVDS3 platformuna taşındı; anahtar artık URL'de değil "key" header'ında gönderiliyor
        _httpClient.BaseAddress = new Uri("https://evds3.tcmb.gov.tr/igmevdsms-dis/");
    }

    private static string ResolveSeriesCode(string symbol, string investmentType)
    {
        var map = investmentType.Equals("gold", StringComparison.OrdinalIgnoreCase)
            ? TcmbSeriesMap.GoldSeriesCodes
            : TcmbSeriesMap.CurrencySeriesCodes;

        if (!map.TryGetValue(symbol, out var seriesCode))
            throw new ExternalServiceException($"TCMB EVDS'de '{symbol}' için tanımlı bir seri kodu yok.");

        return seriesCode;
    }

    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var to = DateTime.Today;
        // Döviz için hafta sonu/tatil boşluklarını, altın için aylık serinin yayın gecikmesini kapsayacak kadar geriden başla
        var lookbackDays = investmentType.Equals("gold", StringComparison.OrdinalIgnoreCase) ? 120 : 10;
        var from = to.AddDays(-lookbackDays);
        var bars = await GetHistoricalPricesAsync(symbol, investmentType, from, to, ct);

        if (bars.Count == 0)
            throw new ExternalServiceException($"TCMB EVDS'de '{symbol}' için fiyat bulunamadı.");

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
        var apiKey = _configuration["MarketData:TcmbEvdsApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ExternalServiceException("TCMB EVDS API anahtarı yapılandırılmamış.");

        var seriesCode = ResolveSeriesCode(symbol, investmentType);
        var jsonFieldName = seriesCode.Replace('.', '_');

        // TCMB'nin API'si "?" ayracı kullanmıyor — path doğrudan parametrelerle devam ediyor
        var url = $"series={seriesCode}&startDate={from:dd-MM-yyyy}&endDate={to:dd-MM-yyyy}&type=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("key", apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TCMB EVDS sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("items", out var itemsElement))
            throw new ExternalServiceException($"TCMB EVDS'de '{symbol}' için veri bulunamadı.");

        var bars = new List<PriceBarDto>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (!item.TryGetProperty(jsonFieldName, out var valueElement) || valueElement.ValueKind != JsonValueKind.String)
                continue; // resmi tatil günlerinde değer boş dönebilir

            var valueString = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(valueString) ||
                !decimal.TryParse(valueString, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                continue;

            // "Tarih" alanının biçimi seriye göre değişiyor (günlük: "dd-MM-yyyy", aylık: "yyyy-M") —
            // ikisinde de tutarlı olan UNIXTIME (saniye) alanını kullanmak daha güvenilir.
            var unixSeconds = long.Parse(item.GetProperty("UNIXTIME").GetProperty("$numberLong").GetString()!);
            var date = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Date;

            bars.Add(new PriceBarDto { Date = date, Open = price, High = price, Low = price, Close = price, Volume = 0 });
        }

        return bars.OrderBy(b => b.Date).ToList();
    }
}
