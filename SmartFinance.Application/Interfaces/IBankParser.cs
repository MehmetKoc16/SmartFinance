using SmartFinance.Application.DTOs.PdfImport;

namespace SmartFinance.Application.Interfaces;

public interface IBankParser
{
    /// <summary>Bu parser bu PDF text'ini işleyebilir mi?</summary>
    bool CanParse(string fullText);

    /// <summary>Banka adı</summary>
    string BankName { get; }

    /// <summary>PDF text'inden işlemleri parse et</summary>
    List<ParsedTransactionDto> Parse(string fullText);

    /// <summary>PDF text'inden dönem bilgisi çıkar (opsiyonel)</summary>
    string? ExtractPeriod(string fullText);
}
