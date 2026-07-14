using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class GoldPriceProvider : IPriceProvider
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { "gold" };

    public GoldPriceProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
    }

    // GenelPara — anahtar gerektirmeyen, gerçek zamanlı gram altın fiyatı (GA = Gram Altın satış).
    // Bu API geçmiş veri sunmuyor; teknik analiz için hâlâ TCMB EVDS'nin aylık serisi kullanılıyor.
    public async Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("https://api.genelpara.com/json/?list=altin&sembol=GA", ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("GenelPara altın fiyatı sorgusu başarısız oldu.");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("GA", out var gaElement) ||
            !gaElement.TryGetProperty("satis", out var satisElement))
            throw new ExternalServiceException("GenelPara'da gram altın fiyatı bulunamadı.");

        var satisString = satisElement.GetString();
        if (string.IsNullOrWhiteSpace(satisString) ||
            !decimal.TryParse(satisString, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            throw new ExternalServiceException("GenelPara altın fiyatı ayrıştırılamadı.");

        return new PriceQuoteDto
        {
            Symbol = symbol,
            Price = price,
            AsOf = DateTime.UtcNow,
        };
    }

    public async Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var apiKey = _configuration["MarketData:TcmbEvdsApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ExternalServiceException("TCMB EVDS API anahtarı yapılandırılmamış.");

        if (!TcmbSeriesMap.GoldSeriesCodes.TryGetValue(symbol, out var seriesCode))
            throw new ExternalServiceException($"'{symbol}' için tanımlı bir altın seri kodu yok.");
        var jsonFieldName = seriesCode.Replace('.', '_');

        // TCMB'nin API'si "?" ayracı kullanmıyor — path doğrudan parametrelerle devam ediyor.
        // 2024 sonrası EVDS3'e taşındı; anahtar URL'de değil "key" header'ında gönderiliyor.
        var url = $"https://evds3.tcmb.gov.tr/igmevdsms-dis/series={seriesCode}&startDate={from:dd-MM-yyyy}&endDate={to:dd-MM-yyyy}&type=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("key", apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TCMB EVDS altın geçmiş verisi sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("items", out var itemsElement))
            throw new ExternalServiceException($"TCMB EVDS'de '{symbol}' için veri bulunamadı.");

        var bars = new List<PriceBarDto>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (!item.TryGetProperty(jsonFieldName, out var valueElement) || valueElement.ValueKind != JsonValueKind.String)
                continue; // resmi tatil günlerinde/aylık aralıklarda değer boş dönebilir

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
