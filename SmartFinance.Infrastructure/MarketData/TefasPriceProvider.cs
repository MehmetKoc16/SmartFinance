using System.Text;
using System.Text.Json;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class TefasPriceProvider : IPriceProvider, IHistorySource
{
    private readonly HttpClient _httpClient;
    private readonly IPriceHistoryStore _historyStore;

    // TEFAS tek istekte 1 aylık sınırı SUNUCU TARAFINDA dayatıyor: daha genişini
    // isteyince HTTP 200 ile birlikte "Tarih aralığı 1 ayı aşamaz" hatası dönüyor
    // (26.08.2026'da ölçüldü). 28 gün, ay uzunluğu farklarına karşı güvenli pay.
    private const int MaxDaysPerRequest = 28;

    // Hız sınırı ölçüldü (26.08.2026): 2 saniye arayla atılan isteklerin 5'i geçti,
    // 6.'sı 429 döndü — yani dakikada ~6 istek. Sınır IP başına olduğu için tüm
    // kullanıcılar arasında paylaşılır; bu yüzden kullanıcı istekleri artık
    // buraya hiç gelmiyor, yalnızca gecelik senkron işi geliyor.
    private static readonly TimeSpan InterChunkDelay = TimeSpan.FromSeconds(11);
    private const int MaxRetries = 4;

    public IEnumerable<string> SupportedInvestmentTypes => new[] { InvestmentType };

    // Depoda bu tiple saklaniyor; ayni kod farkli piyasalarda cakisabilecegi icin
    // tekillik sembol + tip uzerinden kuruluyor.
    private const string InvestmentType = "fund";

    public TimeSpan InterSymbolDelay => InterChunkDelay;

    public TefasPriceProvider(HttpClient httpClient, IPriceHistoryStore historyStore)
    {
        _historyStore = historyStore;
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://www.tefas.gov.tr/");
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Origin"))
        {
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.tefas.gov.tr");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Referer"))
        {
            // 2026'da site Next.js'e taşındı, eski BindHistoryInfo kapatıldı — yeni resmi uç nokta bu
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.tefas.gov.tr/tr/fon-verileri");
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

    /// <summary>
    /// Kullanıcı isteklerinin geçtiği yol: önce kendi veritabanımıza bakılır.
    /// TEFAS'a yalnızca depo istenen aralığı kapsamıyorsa (kullanıcı fonu yeni
    /// ekledi) gidilir; sonrasını gecelik senkron işi güncel tutar.
    ///
    /// Böylece 6 aylık grafik ~90 saniye yerine milisaniyelerde dönüyor ve
    /// TEFAS'ın paylaşılan hız kotası kullanıcı trafiğiyle tüketilmiyor.
    /// </summary>
    public Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default)
        => HistoryBackfill.ReadWithBackfillAsync(_historyStore, this, symbol, InvestmentType, from, to, ct);

    /// <summary>
    /// TEFAS'a giden gerçek istek — yalnızca ilk kez görülen fonlarda ve
    /// gecelik senkron işinde çağrılır.
    /// </summary>
    public async Task<IReadOnlyList<PriceBarDto>> FetchDailyBarsAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var bars = new List<PriceBarDto>();
        var chunkStart = from.Date;
        var isFirstChunk = true;

        // Uzun aralıklar TEFAS'ın ~1 aylık istek sınırını aştığı için parçalara bölünüp ardışık çağrılarla çekiliyor
        while (chunkStart <= to.Date)
        {
            if (!isFirstChunk)
                await Task.Delay(InterChunkDelay, ct); // dakikalık istek sınırına takılmamak için parçalar arası bekleme
            isFirstChunk = false;

            var chunkEnd = chunkStart.AddDays(MaxDaysPerRequest - 1);
            if (chunkEnd > to.Date) chunkEnd = to.Date;

            var chunkBars = await FetchChunkAsync(symbol, chunkStart, chunkEnd, ct);
            bars.AddRange(chunkBars);

            chunkStart = chunkEnd.AddDays(1);
        }

        return bars.OrderBy(b => b.Date).ToList();
    }

    private async Task<List<PriceBarDto>> FetchChunkAsync(string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendRequestAsync(symbol, from, to, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt >= MaxRetries)
                    throw new ExternalServiceException($"TEFAS istek sınırına takıldı, tekrar denemeler tükendi: {symbol}");

                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15);
                await Task.Delay(wait, ct);
                continue;
            }

            return await ParseResponseAsync(response, symbol, ct);
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        // Not: TEFAS API'si "sfonTurKod" ve "sFonTurKod" adında, sadece harf büyüklüğü farklı iki ayrı alan
        // bekliyor — System.Text.Json anonim tip/record üzerinden bunu serileştiremediği için Dictionary kullanılıyor.
        var body = new Dictionary<string, object?>
        {
            ["fonTipi"] = "YAT",
            ["fonKodu"] = symbol.ToUpperInvariant(),
            ["aramaMetni"] = null,
            ["fonTurKod"] = null,
            ["fonGrubu"] = null,
            ["sfonTurKod"] = null,
            ["fonTurAciklama"] = null,
            ["kurucuKod"] = null,
            ["basTarih"] = from.ToString("yyyyMMdd"),
            ["bitTarih"] = to.ToString("yyyyMMdd"),
            ["basSira"] = 1,
            ["bitSira"] = 100000,
            ["dil"] = "TR",
            ["sFonTurKod"] = "",
            ["fonKod"] = "",
            ["fonGrup"] = "",
            ["fonUnvanTip"] = "",
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _httpClient.PostAsync("api/funds/fonGnlBlgSiraliGetir", content, ct);
    }

    private static async Task<List<PriceBarDto>> ParseResponseAsync(HttpResponseMessage response, string symbol, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException($"TEFAS geçmiş fiyat sorgusu başarısız oldu: {symbol}");

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("errorMessage", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.String)
        {
            var errorMsg = errorElement.GetString();
            // Hafta sonu/tatil gibi boş aralıklarda TEFAS "out of bounds" benzeri bir mesaj dönebilir — bu gerçek bir hata değil
            var isEmptyRangeMarker = !string.IsNullOrEmpty(errorMsg) &&
                (errorMsg.Contains("out of bounds", StringComparison.OrdinalIgnoreCase) ||
                 errorMsg.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(errorMsg) && !isEmptyRangeMarker)
                throw new ExternalServiceException($"TEFAS API hatası ({symbol}): {errorMsg}");
        }

        if (!doc.RootElement.TryGetProperty("resultList", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Array)
            return new List<PriceBarDto>();

        var bars = new List<PriceBarDto>();
        foreach (var item in resultElement.EnumerateArray())
        {
            if (!item.TryGetProperty("fiyat", out var priceElement) || priceElement.ValueKind != JsonValueKind.Number)
                continue;

            var date = DateTime.Parse(item.GetProperty("tarih").GetString()!);
            var price = priceElement.GetDecimal();

            bars.Add(new PriceBarDto { Date = date, Open = price, High = price, Low = price, Close = price, Volume = 0 });
        }

        return bars;
    }
}
