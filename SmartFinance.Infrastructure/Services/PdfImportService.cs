using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.BankParsers;
using SmartFinance.Infrastructure.Context;
using UglyToad.PdfPig;

namespace SmartFinance.Infrastructure.Services;

public class PdfImportService : IPdfImportService
{
    private readonly SmartFinanceDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly List<IBankParser> _parsers;

    // Bariz keyword → kategori adı eşleştirmeleri
    private static readonly Dictionary<string, string> DefaultKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "MAAS", "Maaş" }, { "UCRET", "Maaş" }, { "AYLIK", "Maaş" },
        { "EFT", "Transfer" }, { "HAVALE", "Transfer" }, { "FAST", "Transfer" },
        { "ATM", "ATM" },
        { "FATURA", "Fatura" }, { "ELEKTRIK", "Fatura" }, { "DOGALGAZ", "Fatura" },
        { "SU FATURA", "Fatura" }, { "INTERNET", "Fatura" },
    };

    public PdfImportService(SmartFinanceDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;

        // Parser'ları öncelik sırasına göre ekle (GenericBankParser en son)
        _parsers = new List<IBankParser>
        {
            new HalkbankParser(),
            new ZiraatParser(),
            new GenericBankParser()
        };
    }

    private int GetUserId() =>
        int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    public async Task<PdfParseResultDto> ParsePdfAsync(Stream pdfStream, string fileName)
    {
        var userId = GetUserId();

        // 1. PdfPig ile text çıkar
        var fullText = ExtractTextFromPdf(pdfStream);

        if (string.IsNullOrWhiteSpace(fullText))
        {
            return new PdfParseResultDto
            {
                BankName = "Bilinmeyen",
                Transactions = new(),
                // İleride Azure OCR burada devreye girecek
            };
        }

        // 2. Doğru parser'ı bul
        var parser = _parsers.FirstOrDefault(p => p.CanParse(fullText))
                     ?? _parsers.Last(); // GenericBankParser

        // 3. Parse et
        var transactions = parser.Parse(fullText);
        var period = parser.ExtractPeriod(fullText);

        // 4. Kategori eşleştirme (öğrenilen + default)
        await ApplyCategoryMappings(transactions, userId);

        // 5. Duplicate kontrolü
        await MarkDuplicates(transactions, userId);

        return new PdfParseResultDto
        {
            Transactions = transactions,
            BankName = parser.BankName,
            Period = period,
            TotalIncome = transactions.Count(t => t.Type == 1 && !t.IsDuplicate),
            TotalExpense = transactions.Count(t => t.Type == 2 && !t.IsDuplicate),
            DuplicateCount = transactions.Count(t => t.IsDuplicate),
        };
    }

    public async Task<int> ConfirmImportAsync(ConfirmImportDto dto)
    {
        var userId = GetUserId();
        var savedCount = 0;

        foreach (var item in dto.Transactions)
        {
            // Transaction kaydet
            var transaction = new Transaction
            {
                Amount = item.Amount,
                Description = item.Description,
                TransactionDate = item.TransactionDate,
                Type = item.Type == 1 ? TransactionType.Income : TransactionType.Expense,
                CategoryId = item.CategoryId ?? 0,
                UserId = userId,
            };

            await _context.Transactions.AddAsync(transaction);

            // Öğrenme: MerchantName + CategoryId varsa eşleştirmeyi kaydet
            if (!string.IsNullOrWhiteSpace(item.MerchantName) && item.CategoryId.HasValue)
            {
                await SaveCategoryMapping(userId, item.MerchantName, item.CategoryId.Value);
            }

            savedCount++;
        }

        await _context.SaveChangesAsync();
        return savedCount;
    }

    // ─── Private Helpers ─────────────────────────────────────────

    private static string ExtractTextFromPdf(Stream pdfStream)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(pdfStream);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private async Task ApplyCategoryMappings(List<ParsedTransactionDto> transactions, int userId)
    {
        // Kullanıcının öğrenilmiş eşleştirmelerini çek
        var userMappings = await _context.Set<CategoryMapping>()
            .Where(m => m.UserId == userId)
            .Include(m => m.Category)
            .ToListAsync();

        // Tüm kategorileri çek (default keyword eşleştirmesi için)
        var allCategories = await _context.Categories
            .Where(c => c.UserId == userId)
            .ToListAsync();

        foreach (var t in transactions)
        {
            if (string.IsNullOrWhiteSpace(t.MerchantName)) continue;

            // 1. Öğrenilmiş eşleştirme var mı?
            var mapping = userMappings.FirstOrDefault(m =>
                t.MerchantName.Contains(m.MerchantKeyword, StringComparison.OrdinalIgnoreCase) ||
                (t.Description?.Contains(m.MerchantKeyword, StringComparison.OrdinalIgnoreCase) ?? false));

            if (mapping != null)
            {
                t.CategoryId = mapping.CategoryId;
                t.CategoryName = mapping.Category?.Name;
                continue;
            }

            // 2. Default keyword eşleştirmesi
            foreach (var kv in DefaultKeywords)
            {
                if (t.Description?.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) == true ||
                    t.MerchantName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var category = allCategories.FirstOrDefault(c =>
                        c.Name.Equals(kv.Value, StringComparison.OrdinalIgnoreCase));
                    if (category != null)
                    {
                        t.CategoryId = category.Id;
                        t.CategoryName = category.Name;
                    }
                    break;
                }
            }
        }
    }

    private async Task MarkDuplicates(List<ParsedTransactionDto> transactions, int userId)
    {
        if (!transactions.Any()) return;

        var minDate = transactions.Min(t => t.TransactionDate).Date;
        var maxDate = transactions.Max(t => t.TransactionDate).Date.AddDays(1);

        // Aynı dönemdeki mevcut işlemleri çek
        var existingTransactions = await _context.Transactions
            .Where(t => t.UserId == userId
                && t.TransactionDate >= minDate
                && t.TransactionDate < maxDate)
            .ToListAsync();

        foreach (var t in transactions)
        {
            t.IsDuplicate = existingTransactions.Any(e =>
                e.TransactionDate.Date == t.TransactionDate.Date
                && e.Amount == t.Amount
                && (e.Description?.Contains(t.MerchantName ?? "", StringComparison.OrdinalIgnoreCase) ?? false));
        }
    }

    private async Task SaveCategoryMapping(int userId, string merchantName, int categoryId)
    {
        // Zaten varsa güncelle, yoksa ekle
        var existing = await _context.Set<CategoryMapping>()
            .FirstOrDefaultAsync(m => m.UserId == userId
                && m.MerchantKeyword == merchantName);

        if (existing != null)
        {
            existing.CategoryId = categoryId;
            existing.UpdatedDate = DateTime.UtcNow;
        }
        else
        {
            await _context.Set<CategoryMapping>().AddAsync(new CategoryMapping
            {
                MerchantKeyword = merchantName,
                CategoryId = categoryId,
                UserId = userId,
            });
        }
    }
}
