using System.Globalization;
using System.Text.RegularExpressions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.BankParsers;

public class ZiraatParser : IBankParser
{
    public string BankName => "Ziraat Bankası";

    public bool CanParse(string fullText)
    {
        return fullText.Contains("Ziraat", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("ziraatbank", StringComparison.OrdinalIgnoreCase);
    }

    public string? ExtractPeriod(string fullText)
    {
        var match = Regex.Match(fullText, @"(\d{2}\.\d{2}\.\d{4})\s*-\s*(\d{2}\.\d{2}\.\d{4})");
        return match.Success ? match.Value : null;
    }

    public List<ParsedTransactionDto> Parse(string fullText)
    {
        var transactions = new List<ParsedTransactionDto>();
        var lines = fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Ziraat text formatı: dd.MM.yyyy ile başlayan satırlar
        var datePattern = new Regex(@"^(\d{2}\.\d{2}\.\d{4})");
        // Tutar formatı: -174 veya 5000 veya -1.234,56
        var amountPattern = new Regex(@"(-?[\d.]+(?:,\d{1,2})?)");

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var dateMatch = datePattern.Match(trimmed);
            if (!dateMatch.Success) continue;

            if (!DateTime.TryParseExact(dateMatch.Value, "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            // Tutarları bul
            var remaining = trimmed[dateMatch.Length..].Trim();
            var amountMatches = amountPattern.Matches(remaining);
            if (amountMatches.Count < 1) continue;

            // Son iki sayı: işlem tutarı ve bakiye
            // veya sadece bir sayı varsa: o işlem tutarı
            string amountStr;
            if (amountMatches.Count >= 2)
            {
                // Sondan ikinci = tutar, son = bakiye
                amountStr = amountMatches[^2].Value;
            }
            else
            {
                amountStr = amountMatches[0].Value;
            }

            amountStr = amountStr.Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(amountStr, CultureInfo.InvariantCulture, out var amount))
                continue;

            // Açıklama — tarih ve sayıları çıkar
            var description = remaining;
            foreach (Match m in amountMatches)
                description = description.Replace(m.Value, "");
            description = Regex.Replace(description, @"\s+", " ").Trim();

            // İşyeri adını çıkar — Ziraat'te İŞYERİ: X MUTABAKAT: formatı var
            var merchantName = ExtractMerchantName(description);

            transactions.Add(new ParsedTransactionDto
            {
                TransactionDate = date,
                Amount = Math.Abs(amount),
                Description = description,
                MerchantName = merchantName,
                Type = amount >= 0 ? 1 : 2,
            });
        }

        return transactions;
    }

    private static string? ExtractMerchantName(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        // Ziraat'te İŞYERİ: X MUTABAKAT: formatı
        var merchantMatch = Regex.Match(description,
            @"(?:İŞYERİ|ISYERI|IYERI):\s*(.*?)(?:\s*MUTABAKAT|\s*$)",
            RegexOptions.IgnoreCase);
        if (merchantMatch.Success)
            return merchantMatch.Groups[1].Value.Trim();

        // Gönd: X formatı (havale/EFT)
        var senderMatch = Regex.Match(description, @"G[öo]nd:\s*(.*?)(?:\s+\d|$)", RegexOptions.IgnoreCase);
        if (senderMatch.Success)
            return senderMatch.Groups[1].Value.Trim();

        // Fallback: ilk 50 karakter
        var cleaned = description.Trim();
        if (cleaned.Length > 50) cleaned = cleaned[..50].Trim();
        return cleaned;
    }
}
