using System.Globalization;
using System.Text.RegularExpressions;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// Hiçbir banka parser'ı eşleşmezse kullanılan genel parser.
/// dd.MM.yyyy veya dd-MM-yyyy tarih ve Türk tutar formatlarını algılar.
/// </summary>
public class GenericBankParser : IBankParser
{
    public string BankName => "Bilinmeyen Banka";

    public bool CanParse(string fullText) => true; // Her zaman son fallback

    public string? ExtractPeriod(string fullText) => null;

    public List<ParsedTransactionDto> Parse(string fullText)
    {
        var transactions = new List<ParsedTransactionDto>();
        var lines = fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Genel tarih pattern'i: dd.MM.yyyy veya dd-MM-yyyy veya dd/MM/yyyy
        var datePattern = new Regex(@"(\d{2}[.\-/]\d{2}[.\-/]\d{4})");
        // Tutar: -1.234,56 veya 1234,56 veya -40,00
        var amountPattern = new Regex(@"(-?[\d.]+,\d{2})");

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var dateMatch = datePattern.Match(trimmed);
            if (!dateMatch.Success) continue;

            var dateStr = dateMatch.Value.Replace("-", ".").Replace("/", ".");
            if (!DateTime.TryParseExact(dateStr, "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            var amountMatches = amountPattern.Matches(trimmed);
            if (amountMatches.Count < 1) continue;

            var amountStr = amountMatches[0].Value.Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(amountStr, CultureInfo.InvariantCulture, out var amount))
                continue;

            // Açıklama — tarih ve sayıları çıkar
            var description = trimmed;
            description = datePattern.Replace(description, "");
            foreach (Match m in amountMatches)
                description = description.Replace(m.Value, "");
            // Referans/kart numaralarını temizle (5+ haneli sayı dizileri)
            description = Regex.Replace(description, @"\b\d{5,}\b", "");
            description = Regex.Replace(description, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(description)) continue;

            transactions.Add(new ParsedTransactionDto
            {
                TransactionDate = date,
                Amount = Math.Abs(amount),
                Description = description,
                MerchantName = description.Length > 50 ? description[..50].Trim() : description,
                Type = amount >= 0 ? 1 : 2,
            });
        }

        return transactions;
    }
}
