using System.Globalization;
using System.Text.RegularExpressions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.BankParsers;

public class HalkbankParser : IBankParser
{
    public string BankName => "Halkbank";

    public bool CanParse(string fullText)
    {
        return fullText.Contains("Halk Bankas", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("halkbank", StringComparison.OrdinalIgnoreCase);
    }

    public string? ExtractPeriod(string fullText)
    {
        // Dönem: 01.05.2026-08.05.2026 formatı
        var match = Regex.Match(fullText, @"(\d{2}\.\d{2}\.\d{4})\s*-\s*(\d{2}\.\d{2}\.\d{4})");
        return match.Success ? match.Value : null;
    }

    public List<ParsedTransactionDto> Parse(string fullText)
    {
        var transactions = new List<ParsedTransactionDto>();
        var lines = fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Halkbank formatı: satır satır, tarih ile başlayan satırlar işlem satırı
        // Tarih formatı: dd-MM-yyyy
        var datePattern = new Regex(@"^(\d{2}-\d{2}-\d{4})");
        // Tutar formatı: -40,00 veya 1.000,50
        var amountPattern = new Regex(@"(-?[\d.]+,\d{2})");

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var dateMatch = datePattern.Match(trimmed);
            if (!dateMatch.Success) continue;

            if (!DateTime.TryParseExact(dateMatch.Value, "dd-MM-yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            // Tutarları bul — ilk tutar işlem tutarı, ikincisi bakiye
            var amountMatches = amountPattern.Matches(trimmed);
            if (amountMatches.Count < 1) continue;

            var amountStr = amountMatches[0].Value
                .Replace(".", "")    // binlik ayracı kaldır
                .Replace(",", ".");  // ondalık ayracını noktaya çevir

            if (!decimal.TryParse(amountStr, CultureInfo.InvariantCulture, out var amount))
                continue;

            // Açıklamayı çıkar — tarih ve tutarlardan sonraki kısım
            var description = trimmed;
            // Tarihi kaldır
            description = datePattern.Replace(description, "").Trim();
            // Tutarları ve bakiyeyi kaldır
            foreach (Match m in amountMatches)
                description = description.Replace(m.Value, "").Trim();

            // İşyeri adını basitçe açıklamanın kendisi yap (Halkbank'ta İŞYERİ: yok)
            var merchantName = ExtractMerchantName(description);

            transactions.Add(new ParsedTransactionDto
            {
                TransactionDate = date,
                Amount = Math.Abs(amount),
                Description = description,
                MerchantName = merchantName,
                Type = amount >= 0 ? 1 : 2,  // 1=Income, 2=Expense
            });
        }

        return transactions;
    }

    private static string? ExtractMerchantName(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        // Halkbank açıklamaları genelde kısa ve direkt: "TURNIKE GEÇİŞ", "POS ALIŞVERİŞ..."
        // İlk anlamlı kısmı al
        var cleaned = description.Trim();
        // Çok uzunsa ilk 50 karakteri al
        if (cleaned.Length > 50)
            cleaned = cleaned[..50].Trim();
        return cleaned;
    }
}
