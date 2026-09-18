using System.Globalization;
using ClosedXML.Excel;

namespace SmartFinance.Infrastructure.Fundamentals;

public sealed record KapFinancialRow(
    string CompanyTitle, string DisclosureId, DateTime PublishedAt, int Year, int Period,
    bool Consolidated, decimal? NetProfitParent, decimal? EquityParent);

/// <summary>
/// KAP "Finansal Tablo Kalem Sorgulama" Excel ciktisini okur. Duzen (18.09.2026):
/// once baslik/aciklama satirlari, sonra "Sirket | Bildirim ID | Bildirim Yayin
/// Tarihi | Yil | Periyot | Finansal Tablo Niteligi | Sunum Para Birimi | ... |
/// kalemler". Degerler Turkce bicimli metin ("1.018.517") ve sunum biriminde
/// ("1000000TL") — birim satir satir okunur, varsayilmaz (sirkete gore degisiyor).
/// </summary>
public static class KapExcelParser
{
    public static IReadOnlyList<KapFinancialRow> Parse(Stream xlsx)
    {
        using var workbook = new XLWorkbook(xlsx);
        var sheet = workbook.Worksheet(1);

        var baslik = sheet.RowsUsed().FirstOrDefault(r => r.Cell(1).GetString().Trim() == "Şirket")
            ?? throw new FormatException("KAP tablosunda 'Şirket' başlık satırı bulunamadı.");
        var kolonlar = baslik.CellsUsed()
            .GroupBy(c => c.GetString().Trim())
            .ToDictionary(g => g.Key, g => g.First().Address.ColumnNumber);

        int Kolon(string ad) => kolonlar.TryGetValue(ad, out var i)
            ? i : throw new FormatException($"KAP tablosunda '{ad}' kolonu yok.");
        int? Bul(Func<string, bool> kosul)
        {
            var eslesen = kolonlar.Where(k => kosul(k.Key)).Select(k => (int?)k.Value);
            return eslesen.FirstOrDefault();
        }

        var sirketK = Kolon("Şirket");
        var bildirimK = Kolon("Bildirim ID");
        var tarihK = Kolon("Bildirim Yayın Tarihi");
        var yilK = Kolon("Yıl");
        var periyotK = Kolon("Periyot");
        var nitelikK = Kolon("Finansal Tablo Niteliği");
        var birimK = Kolon("Sunum Para Birimi");
        // Etiketlerde sapka isareti tutarsiz olabiliyor ("Kârının"), o yuzden
        // tam esitlik yerine ayirt edici parcalara bakiliyor.
        var karK = Bul(k => k.StartsWith("Dönem", StringComparison.Ordinal) && k.Contains("Ana Ortaklık Payları"));
        var ozkaynakK = Bul(k => k.StartsWith("Ana Ortaklığa Ait Özkaynak", StringComparison.Ordinal));

        var satirlar = new List<KapFinancialRow>();
        foreach (var satir in sheet.RowsUsed().Where(r => r.RowNumber() > baslik.RowNumber()))
        {
            var sirket = satir.Cell(sirketK).GetString().Trim();
            if (sirket.Length == 0) continue;

            var carpan = BirimCarpani(satir.Cell(birimK).GetString());
            if (carpan is null) continue; // taninmayan birim: yanlis olcekle kaydetmektense atla

            satirlar.Add(new KapFinancialRow(
                sirket,
                satir.Cell(bildirimK).GetString().Trim(),
                DateTime.ParseExact(satir.Cell(tarihK).GetString().Trim(), "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                Tamsayi(satir.Cell(yilK)),
                Tamsayi(satir.Cell(periyotK)),
                KonsolideMi(satir.Cell(nitelikK).GetString()),
                karK is int k ? Sayi(satir.Cell(k)) * carpan : null,
                ozkaynakK is int o ? Sayi(satir.Cell(o)) * carpan : null));
        }
        return satirlar;
    }

    /// "1000000TL" -> 1.000.000, "1000TL" -> 1.000, "TL" -> 1. Baska bir sey -> null.
    public static decimal? BirimCarpani(string birim)
    {
        var s = birim.Trim().Replace(".", "").Replace(" ", "");
        var rakamlar = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (!s[rakamlar.Length..].Equals("TL", StringComparison.OrdinalIgnoreCase)) return null;
        return rakamlar.Length == 0 ? 1m : decimal.Parse(rakamlar, CultureInfo.InvariantCulture);
    }

    /// Turkce bicim: "1.018.517", "-1.818", "(1.234)", "12,5". Bos veya "-" -> null.
    internal static decimal? Sayi(IXLCell hucre)
    {
        if (hucre.DataType == XLDataType.Number) return (decimal)hucre.GetDouble();
        var s = hucre.GetString().Trim();
        if (s.Length == 0 || s == "-") return null;
        var eksi = s.StartsWith('(') && s.EndsWith(')');
        s = s.Trim('(', ')').Replace(".", "").Replace(",", ".");
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
            return null;
        return eksi ? -d : d;
    }

    private static int Tamsayi(IXLCell hucre) => hucre.DataType == XLDataType.Number
        ? (int)hucre.GetDouble()
        : int.Parse(hucre.GetString().Trim(), CultureInfo.InvariantCulture);

    private static bool KonsolideMi(string nitelik) =>
        nitelik.Contains("Konsolide", StringComparison.OrdinalIgnoreCase)
        && !nitelik.Contains("Olmayan", StringComparison.OrdinalIgnoreCase);
}
