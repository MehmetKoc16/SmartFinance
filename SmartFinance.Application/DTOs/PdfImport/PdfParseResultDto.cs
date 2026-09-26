namespace SmartFinance.Application.DTOs.PdfImport;

public class PdfParseResultDto
{
    public List<ParsedTransactionDto> Transactions { get; set; } = new();
    public string? BankName { get; set; }
    public string? Period { get; set; }
    public int TotalIncome { get; set; }
    public int TotalExpense { get; set; }
    public int DuplicateCount { get; set; }

    // PDF'te gomulu metin yok (taranmis ekstre). Telefon sayfalari kendisi
    // OCR ile okuyup kelimeleri /pdfimport/parse-words ucuna gonderir.
    public bool NeedsOcr { get; set; }
}

public class ParsedTransactionDto
{
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? MerchantName { get; set; }
    public DateTime TransactionDate { get; set; }
    public int Type { get; set; }              // 1=Income, 2=Expense
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public bool IsDuplicate { get; set; }      // Zaten kayıtlı mı?

    // Ekstrede bakiye sutunu varsa: onceki bakiye +/- bu tutar, bu satirin
    // bakiyesini vermiyor. Tutar veya tur yanlis okunmus olabilir; kullanici
    // inceleme ekraninda kontrol etmeli.
    public bool BalanceMismatch { get; set; }
}
