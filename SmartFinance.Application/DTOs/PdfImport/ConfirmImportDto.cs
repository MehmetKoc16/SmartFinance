using SmartFinance.Application.DTOs.Transaction;

namespace SmartFinance.Application.DTOs.PdfImport;

public class ConfirmImportDto
{
    public List<ConfirmTransactionItemDto> Transactions { get; set; } = new();
}

/// <summary>
/// CreateTransactionDto'nun alanları + öğrenme için MerchantName.
/// CategoryId burada opsiyonel çünkü kullanıcı "Kategorisiz" bırakabilir.
/// </summary>
public class ConfirmTransactionItemDto
{
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public int Type { get; set; }           // 1=Income, 2=Expense
    public int? CategoryId { get; set; }
    public string? MerchantName { get; set; } // Öğrenme sistemi için
}
