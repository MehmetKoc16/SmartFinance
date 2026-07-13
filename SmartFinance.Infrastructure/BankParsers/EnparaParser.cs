using System.Globalization;
using System.Text.RegularExpressions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// Enpara.com ekstre formatı: dd/MM/yy (2 haneli yıl) tarih, "Tarih Açıklama Tutar Bakiye"
/// sütun sırası, tutarlarda bazen eksi işaretiyle sayı arasında boşluk var ("- 5,28 TL").
///
/// Açıklama 2 satıra bölündüğünde, tarih+tutar satırı PdfPig çıktısında açıklamanın İKİ
/// PARÇASININ ARASINA düşüyor (tutar, sarılı metnin dikey ortasına hizalı olduğu için):
///   Vergi Kesintisi, ... faizi     &lt;- açıklama parça 1 (tarihsiz)
///   01/04/26 - 0,79 TL -4,27 TL    &lt;- tarih + tutar, açıklama YOK
///   KKDF                           &lt;- açıklama parça 2 (tarihsiz)
/// Bu yüzden satır bazlı "önceki satırı devam say" mantığı değil, tarih+tutar satırının
/// hem ÖNCESİNE hem SONRASINA bakan bir yaklaşım gerekiyor.
/// </summary>
public class EnparaParser : IBankParser
{
    public string BankName => "Enpara";

    public bool CanParse(string fullText) => fullText.Contains("Enpara", StringComparison.OrdinalIgnoreCase);

    public string? ExtractPeriod(string fullText)
    {
        var match = Regex.Match(fullText, @"(\d{2}/\d{2}/\d{4})\s*-\s*(\d{2}/\d{2}/\d{4})");
        return match.Success ? match.Value : null;
    }

    public List<ParsedTransactionDto> Parse(string fullText)
    {
        var transactions = new List<ParsedTransactionDto>();
        var lines = fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Enpara tarihi: dd/MM/yy (2 haneli yıl), satır başında
        var datePattern = new Regex(@"^(\d{2}/\d{2}/\d{2})\b");
        // Tutar: -1.234,56 / 1234,56 / "- 5,28" (eksi işaretiyle sayı arasında boşluk olabilir)
        var amountPattern = new Regex(@"(-?\s?[\d.]+,\d{2})");

        string? lastOrphanLine = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i];
            var dateMatch = datePattern.Match(trimmed);

            if (!dateMatch.Success)
            {
                // Tarihsiz satır — bir sonraki tarih+tutar satırının açıklama parçası olabilir
                lastOrphanLine = trimmed;
                continue;
            }

            if (!DateTime.TryParseExact(dateMatch.Value, "dd/MM/yy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                lastOrphanLine = null;
                continue;
            }

            var remaining = trimmed[dateMatch.Length..].Trim();
            var amountMatches = amountPattern.Matches(remaining);
            if (amountMatches.Count < 1)
            {
                // Tutarsız tarih satırı (beklenmiyor ama savunma amaçlı atlanıyor)
                lastOrphanLine = null;
                continue;
            }

            var amountStr = amountMatches.Count >= 2
                ? amountMatches[^2].Value
                : amountMatches[0].Value;
            amountStr = amountStr.Replace(" ", "").Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(amountStr, CultureInfo.InvariantCulture, out var amount))
            {
                lastOrphanLine = null;
                continue;
            }

            // Tarih+tutar satırındaki gerçek açıklama parçası (varsa)
            var inlineDesc = remaining;
            foreach (Match m in amountMatches)
                inlineDesc = inlineDesc.Replace(m.Value, "");
            inlineDesc = Regex.Replace(inlineDesc, @"\bTL\b", "", RegexOptions.IgnoreCase);
            inlineDesc = Regex.Replace(inlineDesc, @"\s+", " ").Trim();

            string description;
            if (!string.IsNullOrWhiteSpace(inlineDesc))
            {
                // Tek satırlık işlem — açıklama zaten bu satırda
                description = inlineDesc;
            }
            else
            {
                // İki satıra bölünmüş açıklama: önceki tarihsiz satır (parça 1) +
                // bir sonraki tarihsiz satır varsa (parça 2)
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(lastOrphanLine))
                    parts.Add(lastOrphanLine);

                if (i + 1 < lines.Count && !datePattern.IsMatch(lines[i + 1]))
                {
                    parts.Add(lines[i + 1]);
                    i++; // sonraki satır tüketildi, ana döngüde tekrar işlenmesin
                }

                description = string.Join(" ", parts);
            }

            description = Regex.Replace(description, @"\b\d{5,}\b", "");
            description = Regex.Replace(description, @"\s+", " ").Trim();

            transactions.Add(new ParsedTransactionDto
            {
                TransactionDate = date,
                Amount = Math.Abs(amount),
                Description = description,
                MerchantName = ExtractMerchantName(description),
                Type = amount >= 0 ? 1 : 2,
            });

            lastOrphanLine = null;
        }

        return transactions;
    }

    private static string? ExtractMerchantName(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        // "Gelen Transfer, İSİM, ..." / "Giden Transfer, İSİM, ..." formatı
        var transferMatch = Regex.Match(description,
            @"^(?:Gelen|Giden)\s+Transfer,\s*(.*?)(?:,|$)", RegexOptions.IgnoreCase);
        if (transferMatch.Success)
        {
            var name = transferMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        // Fallback: ilk 50 karakter
        var cleaned = description.Trim();
        if (cleaned.Length > 50) cleaned = cleaned[..50].Trim();
        return cleaned;
    }
}
