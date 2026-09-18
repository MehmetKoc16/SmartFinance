using System.Net;
using System.Text;
using System.Text.Json;

namespace SmartFinance.Infrastructure.Fundamentals;

public class KapRateLimitException(string message) : Exception(message);

public interface IKapClient
{
    Task<IReadOnlyDictionary<string, KapCompany>> GetCompaniesAsync(CancellationToken ct = default);

    /// En fazla 2 yil (KAP arayuzunun siniri). Dort donemin hepsi istenir.
    Task<IReadOnlyList<KapFinancialRow>> GetFinancialsAsync(KapCompany company, IReadOnlyList<int> years, CancellationToken ct = default);
}

/// <summary>
/// KAP'in kendi sitesinin kullandigi uclar (18.09.2026'da sayfanin kodundan
/// cikarildi, kimlik dogrulamasi istemiyor): sirket listesi sayfasi ve
/// "Finansal Tablo Kalem Sorgulama"nin Excel disa aktarimi.
/// </summary>
public class KapClient : IKapClient
{
    public const string NetProfitItemId = "ifrs-full_ProfitLossAttributableToOwnersOfParent";
    public const string EquityItemId = "ifrs-full_EquityAttributableToOwnersOfParent";

    private readonly HttpClient _http;

    public KapClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://www.kap.org.tr/");
        _http.Timeout = TimeSpan.FromSeconds(120);
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        if (!_http.DefaultRequestHeaders.Contains("Accept-Language"))
            _http.DefaultRequestHeaders.Add("Accept-Language", "tr");
    }

    public async Task<IReadOnlyDictionary<string, KapCompany>> GetCompaniesAsync(CancellationToken ct = default)
    {
        using var yanit = await _http.GetAsync("tr/bist-sirketler", ct);
        HataVarsaFirlat(yanit);
        return KapCompanyMapParser.Parse(await yanit.Content.ReadAsStringAsync(ct));
    }

    public async Task<IReadOnlyList<KapFinancialRow>> GetFinancialsAsync(
        KapCompany company, IReadOnlyList<int> years, CancellationToken ct = default)
    {
        // Unvan listesi zorunlu: bos gonderilince KAP HTTP 500 donuyor.
        var govde = JsonSerializer.Serialize(new
        {
            companyType = "IGS",
            mkkMemberIdList = new[] { company.Oid },
            mkkMemberTitleList = new[] { company.Title },
            yearList = years.Select(y => y.ToString()).ToArray(),
            periodList = new[] { "1", "2", "3", "4" },
            itemIdList = new[] { NetProfitItemId, EquityItemId },
            sectors = new[] { "GENERAL" },
        });
        using var yanit = await _http.PostAsync("tr/api/export/compareItems",
            new StringContent(govde, Encoding.UTF8, "application/json"), ct);
        HataVarsaFirlat(yanit);
        await using var akis = await yanit.Content.ReadAsStreamAsync(ct);
        var bellek = new MemoryStream();
        await akis.CopyToAsync(bellek, ct);
        bellek.Position = 0;
        return KapExcelParser.Parse(bellek);
    }

    private static void HataVarsaFirlat(HttpResponseMessage yanit)
    {
        // 429 gecici dalgalanma degil "yavasla" demek; israr etmek yasaklanmaya
        // goturur. Cagiran bu turu birakip sonraki tura kalir.
        if (yanit.StatusCode == HttpStatusCode.TooManyRequests)
            throw new KapRateLimitException("KAP hız sınırı (HTTP 429).");
        if (!yanit.IsSuccessStatusCode)
            throw new HttpRequestException($"KAP isteği başarısız: HTTP {(int)yanit.StatusCode} ({yanit.RequestMessage?.RequestUri})");
    }
}
