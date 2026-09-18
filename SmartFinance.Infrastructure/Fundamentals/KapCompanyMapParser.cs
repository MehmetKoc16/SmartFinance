using System.Text.RegularExpressions;

namespace SmartFinance.Infrastructure.Fundamentals;

public sealed record KapCompany(string Oid, string Title);

/// <summary>
/// kap.org.tr/tr/bist-sirketler sayfasina kacisli JSON olarak gomulu gelen sirket
/// listesinden hisse kodu -> (KAP kimligi, KAP unvani) eslemesi. Yontem
/// otomatik-al-sat projesindeki fetch_kap.sirket_haritasi'ndan. Bir sirketin
/// birden fazla hisse kodu olabilir ("ISCTR, ISATR").
/// </summary>
public static class KapCompanyMapParser
{
    private static readonly Regex Kayit = new(
        "\"mkkMemberOid\":\"(?<oid>[0-9a-zA-Z]+)\"[^{}]*?\"kapMemberTitle\":\"(?<title>[^\"]*)\"[^{}]*?\"stockCode\":\"(?<codes>[A-Z0-9,\\s]+)\"",
        RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, KapCompany> Parse(string html)
    {
        var duz = html.Replace("\\\"", "\"");
        var harita = new Dictionary<string, KapCompany>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Kayit.Matches(duz))
        {
            var sirket = new KapCompany(m.Groups["oid"].Value, m.Groups["title"].Value);
            foreach (var kod in m.Groups["codes"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                harita.TryAdd(kod, sirket);
        }

        // Sayfa duzeni degisirse sessizce bos harita donmek, tum F/K'larin
        // "veri yok"a dusmesi demek; hata gorunur olmali.
        if (harita.Count < 100)
            throw new InvalidOperationException($"KAP sirket listesi supheli: yalnizca {harita.Count} hisse kodu bulundu.");
        return harita;
    }
}
