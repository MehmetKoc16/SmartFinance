using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SmartFinance.Application.DTOs.PdfImport;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// Ziraat internet bankacılığının "Hesap Hareketleri" Excel (.xlsx) dışa aktarımını okur.
/// Sütunlar (Tarih/Açıklama/İşlem Tutarı) zaten tipli hücreler halinde geldiği için
/// PDF parser'ların aksine tutar/tarih için regex tahmini gerekmiyor — yalnızca işyeri
/// adı için ZiraatParser'ın Açıklama-ayrıştırma kalıpları paylaşılıyor.
/// </summary>
public class ZiraatExcelParser
{
    public string BankName => "Ziraat Bankası";

    public string? ExtractPeriod(IXLWorksheet sheet)
    {
        foreach (var cell in sheet.CellsUsed())
        {
            if (cell.DataType != XLDataType.Text) continue;
            var match = Regex.Match(cell.GetString(), @"(\d{1,2}\.\d{1,2}\.\d{4})\s*-\s*(\d{1,2}\.\d{1,2}\.\d{4})");
            if (match.Success) return match.Value;
        }
        return null;
    }

    public List<ParsedTransactionDto> Parse(IXLWorksheet sheet)
    {
        var transactions = new List<ParsedTransactionDto>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

        // Tarih sütunu (A) dd.MM.yyyy ile eşleşen satırlar işlem satırıdır — başlık/altbilgi
        // bloklarını ayrıca tespit etmeye gerek bırakmıyor, tarih formatı doğal bir filtre.
        for (var row = 1; row <= lastRow; row++)
        {
            var dateCell = sheet.Cell(row, 1);
            if (!DateTime.TryParseExact(dateCell.GetString().Trim(), "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            var amountCell = sheet.Cell(row, 4);
            if (amountCell.DataType != XLDataType.Number) continue;

            var description = sheet.Cell(row, 3).GetString().Trim();
            var amount = (decimal)amountCell.GetDouble();

            transactions.Add(new ParsedTransactionDto
            {
                TransactionDate = date,
                Amount = Math.Abs(amount),
                Description = description,
                MerchantName = ZiraatParser.ExtractMerchantName(description),
                Type = amount >= 0 ? 1 : 2,
            });
        }

        return transactions;
    }
}
