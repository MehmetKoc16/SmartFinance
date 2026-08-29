using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<PdfImportService> _logger;
    private readonly List<IBankParser> _parsers;
    private readonly ZiraatExcelParser _excelParser = new();

    // Bariz keyword → kategori adı eşleştirmeleri. AuthService'te her yeni kullanıcıya
    // acilan 8 varsayilan kategoriyle (Maaş/Yeme-İçme/Ulaşım/Fatura/ATM/Transfer/
    // Alışveriş/Diğer) hizali tutulmali. Banka ekstresi metinleri bazen Türkçe
    // karakterleri koruyor (Excel) bazen ASCII'ye indirgiyor (bazı PDF'ler) — bu yuzden
    // cogu anahtar hem aksanli hem aksansiz eklendi.
    private static readonly Dictionary<string, string> DefaultKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "MAAS", "Maaş" }, { "MAAŞ", "Maaş" }, { "UCRET", "Maaş" }, { "ÜCRET", "Maaş" }, { "AYLIK", "Maaş" },
        { "EFT", "Transfer" }, { "HAVALE", "Transfer" }, { "FAST", "Transfer" }, { "VIRMAN", "Transfer" },
        { "ATM", "ATM" },
        { "FATURA", "Fatura" }, { "ELEKTRIK", "Fatura" }, { "ELEKTRİK", "Fatura" }, { "DOGALGAZ", "Fatura" },
        { "DOĞALGAZ", "Fatura" }, { "SU FATURA", "Fatura" }, { "INTERNET", "Fatura" }, { "İNTERNET", "Fatura" },
        { "TELEFON", "Fatura" }, { "TURKCELL", "Fatura" }, { "VODAFONE", "Fatura" }, { "TURK TELEKOM", "Fatura" },
        { "BSMV", "Fatura" }, { "KOMISYON", "Fatura" }, { "KOMİSYON", "Fatura" }, { "MASRAF", "Fatura" },

        // Yeme-İçme: genel kelimeler + yaygın zincirler
        { "YEMEK", "Yeme-İçme" }, { "RESTORAN", "Yeme-İçme" }, { "RESTAURANT", "Yeme-İçme" },
        { "CAFE", "Yeme-İçme" }, { "KAFE", "Yeme-İçme" }, { "LOKANTA", "Yeme-İçme" },
        { "PIDE", "Yeme-İçme" }, { "PİDE", "Yeme-İçme" }, { "KEBAP", "Yeme-İçme" }, { "KEBAB", "Yeme-İçme" },
        { "SIMIT", "Yeme-İçme" }, { "SİMİT", "Yeme-İçme" }, { "PASTANE", "Yeme-İçme" }, { "FIRIN", "Yeme-İçme" },
        { "BALIK", "Yeme-İçme" }, { "PIZZA", "Yeme-İçme" }, { "BURGER", "Yeme-İçme" },
        { "STARBUCKS", "Yeme-İçme" }, { "MCDONALD", "Yeme-İçme" }, { "KFC", "Yeme-İçme" },
        { "DOMINO", "Yeme-İçme" }, { "SUBWAY", "Yeme-İçme" }, { "YEMEKSEPETI", "Yeme-İçme" },
        { "YEMEKSEPETİ", "Yeme-İçme" }, { "GETIR YEMEK", "Yeme-İçme" }, { "GETİR YEMEK", "Yeme-İçme" },
        { "TRENDYOL YEMEK", "Yeme-İçme" }, { "CIKOLATA", "Yeme-İçme" }, { "ÇİKOLATA", "Yeme-İçme" },

        // Ulaşım: akaryakıt, taksi, toplu taşıma, uçak
        { "BENZIN", "Ulaşım" }, { "BENZİN", "Ulaşım" }, { "PETROL", "Ulaşım" }, { "AKARYAKIT", "Ulaşım" },
        { "OPET", "Ulaşım" }, { "SHELL", "Ulaşım" }, { " BP ", "Ulaşım" }, { "TOTAL ENERJI", "Ulaşım" },
        { "OTOPARK", "Ulaşım" }, { "TAKSI", "Ulaşım" }, { "TAXI", "Ulaşım" }, { "UBER", "Ulaşım" },
        { "BITAKSI", "Ulaşım" }, { "BİTAKSİ", "Ulaşım" }, { "METROBUS", "Ulaşım" }, { "METROBÜS", "Ulaşım" },
        { "OTOBUS", "Ulaşım" }, { "OTOBÜS", "Ulaşım" }, { "AKBIL", "Ulaşım" }, { "ISTANBULKART", "Ulaşım" },
        { "İSTANBULKART", "Ulaşım" }, { "THY", "Ulaşım" }, { "PEGASUS", "Ulaşım" }, { "SUNEXPRESS", "Ulaşım" },

        // Alışveriş: market/perakende zincirleri + genel kelimeler
        { "MIGROS", "Alışveriş" }, { "BIM", "Alışveriş" }, { "A101", "Alışveriş" }, { "SOK", "Alışveriş" },
        { "ŞOK", "Alışveriş" }, { "CARREFOUR", "Alışveriş" }, { "MARKET", "Alışveriş" },
        { "MAGAZA", "Alışveriş" }, { "MAĞAZA", "Alışveriş" }, { "TEKNOSA", "Alışveriş" },
        { "MEDIAMARKT", "Alışveriş" }, { "LCW", "Alışveriş" }, { "DEFACTO", "Alışveriş" },
        { "KOTON", "Alışveriş" }, { "TRENDYOL", "Alışveriş" }, { "HEPSIBURADA", "Alışveriş" },
        { "HEPSİBURADA", "Alışveriş" }, { "AMAZON", "Alışveriş" }, { "N11", "Alışveriş" },
        { "GIDA", "Alışveriş" }, { "PLAYSTATION", "Alışveriş" },
        { "ELEKTRONI", "Alışveriş" }, { "ELEKTRONİK", "Alışveriş" },
    };

    public PdfImportService(SmartFinanceDbContext context, ICurrentUserService currentUserService, ILogger<PdfImportService> logger)
    {
        _context = context;
        _currentUserService = currentUserService;
        _logger = logger;

        // Parser'ları öncelik sırasına göre ekle (GenericBankParser en son)
        _parsers = new List<IBankParser>
        {
            new HalkbankParser(),
            new ZiraatParser(),
            new EnparaParser(),
            new GenericBankParser()
        };
    }

    private int GetUserId() =>
        _currentUserService.UserId;

    public async Task<PdfParseResultDto> ParsePdfAsync(Stream fileStream, string fileName)
    {
        _logger.LogInformation("PDF/Excel ice aktarma basladi: {FileName}", fileName);

        List<ParsedTransactionDto> transactions;
        string bankName;
        string? period;

        if (fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            (transactions, bankName, period) = ParseExcel(fileStream);
        }
        else
        {
            (transactions, bankName, period) = ParsePdfText(fileStream);
        }

        _logger.LogInformation("{Count} islem cikarildi, donem: {Period}", transactions.Count, period);

        var userId = GetUserId();

        // Kategori eşleştirme (öğrenilen + default)
        await ApplyCategoryMappings(transactions, userId);

        // Duplicate kontrolü
        await MarkDuplicates(transactions, userId);

        return new PdfParseResultDto
        {
            Transactions = transactions,
            BankName = bankName,
            Period = period,
            TotalIncome = transactions.Count(t => t.Type == 1 && !t.IsDuplicate),
            TotalExpense = transactions.Count(t => t.Type == 2 && !t.IsDuplicate),
            DuplicateCount = transactions.Count(t => t.IsDuplicate),
        };
    }

    private (List<ParsedTransactionDto> Transactions, string BankName, string? Period) ParsePdfText(Stream pdfStream)
    {
        var fullText = ExtractTextFromPdf(pdfStream);
        _logger.LogInformation("PDF metin uzunlugu: {Length}", fullText?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(fullText))
        {
            _logger.LogWarning("PDF'den metin cikarilamadi — goruntu tabanli PDF olabilir");
            return (new(), "Bilinmeyen", null);
        }

        var parser = _parsers.FirstOrDefault(p => p.CanParse(fullText))
                     ?? _parsers.Last(); // GenericBankParser
        _logger.LogInformation("Secilen parser: {BankName}", parser.BankName);

        return (parser.Parse(fullText), parser.BankName, parser.ExtractPeriod(fullText));
    }

    private (List<ParsedTransactionDto> Transactions, string BankName, string? Period) ParseExcel(Stream excelStream)
    {
        using var workbook = new XLWorkbook(excelStream);
        var sheet = workbook.Worksheets.First();
        return (_excelParser.Parse(sheet), _excelParser.BankName, _excelParser.ExtractPeriod(sheet));
    }

    public async Task<ImportResultDto> ConfirmImportAsync(ConfirmImportDto dto)
    {
        var userId = GetUserId();
        var savedCount = 0;
        var skippedCount = 0;

        // Kullanıcının sahip olduğu kategori id'leri — client'tan gelen categoryId
        // başka bir kullanıcıya ait olsa bile kabul edilmesin diye önceden çekiliyor.
        var ownedCategoryIds = (await _context.Categories
            .Where(c => c.UserId == userId)
            .Select(c => c.Id)
            .ToListAsync())
            .ToHashSet();

        var existingCounts = await BuildExistingCountsAsync(userId, dto.Transactions);

        foreach (var item in dto.Transactions)
        {
            // Zaten kayıtlı bir işlemi ikinci kez eklemeyiz. Kullanıcının aynı
            // ekstreyi tekrar yüklemesi sık görülen bir durum ve koruma
            // olmadığında giderler iki katı görünüyordu.
            var key = DuplicateKey(item.TransactionDate, item.Amount, item.Type, item.Description);
            if (existingCounts.TryGetValue(key, out var remaining) && remaining > 0)
            {
                existingCounts[key] = remaining - 1;
                skippedCount++;
                continue;
            }

            var categoryId = item.CategoryId.HasValue && ownedCategoryIds.Contains(item.CategoryId.Value)
                ? item.CategoryId.Value
                : (int?)null;

            // Transaction kaydet
            var transaction = new Transaction
            {
                Amount = item.Amount,
                Description = item.Description,
                MerchantName = item.MerchantName,
                TransactionDate = item.TransactionDate,
                Type = item.Type == 1 ? TransactionType.Income : TransactionType.Expense,
                CategoryId = categoryId,
                UserId = userId,
            };

            await _context.Transactions.AddAsync(transaction);

            // Öğrenme: MerchantName + CategoryId varsa eşleştirmeyi kaydet
            if (!string.IsNullOrWhiteSpace(item.MerchantName) && categoryId.HasValue)
            {
                await SaveCategoryMapping(userId, item.MerchantName, categoryId.Value);
            }

            savedCount++;
        }

        await _context.SaveChangesAsync();
        return new ImportResultDto { SavedCount = savedCount, SkippedCount = skippedCount };
    }

    /// <summary>
    /// Gelen işlemlerle AYNI olan mevcut kayıtları sayar.
    /// </summary>
    /// Neden küme değil de SAYIM: kullanıcı aynı gün, aynı yerden, aynı tutarda
    /// iki alışveriş yapmış olabilir (iki kahve gibi). Küme kullanılsaydı
    /// ikincisi mükerrer sanılıp sessizce yutulurdu. Sayım karşılaştırmasıyla
    /// yalnızca FAZLASI atlanır: veritabanında 1, dosyada 2 varsa 1 tanesi
    /// eklenir; aynı dosya ikinci kez yüklenirse hiçbiri eklenmez.
    ///
    /// Yalnızca gelen işlemlerin tarih aralığı sorgulanır — tüm geçmişi
    /// belleğe çekmek gereksiz olurdu.
    private async Task<Dictionary<string, int>> BuildExistingCountsAsync(
        int userId, List<ConfirmTransactionItemDto> incoming)
    {
        // OrdinalIgnoreCase: anahtarlar buyuk harfe cevrilerek uretilmiyor.
        // ToUpperInvariant Turkce "i" harfini "I" yapip "İ" ile eslesmemesine
        // yol aciyordu — kultur duyarli donusum yerine karsilastirmayi
        // comparer'a birakmak hem dogru hem daha ucuz.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (incoming.Count == 0) return counts;

        var from = incoming.Min(i => i.TransactionDate).Date;
        var to = incoming.Max(i => i.TransactionDate).Date.AddDays(1).AddTicks(-1);

        var existing = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.TransactionDate >= from && t.TransactionDate <= to)
            .Select(t => new { t.TransactionDate, t.Amount, t.Type, t.Description })
            .ToListAsync();

        foreach (var t in existing)
        {
            var key = DuplicateKey(t.TransactionDate, t.Amount, (int)t.Type, t.Description);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        return counts;
    }

    /// Tarih (saat yok) + tutar + yön + açıklama. Açıklama banka ekstresindeki
    /// satırın kendisi olduğu için aynı işlemi en güvenilir ayırt eden alan.
    /// Fazla boşluklar temizlenir; harf büyüklüğü farkını sözlüğün
    /// OrdinalIgnoreCase karşılaştırıcısı absorbe eder.
    private static string DuplicateKey(DateTime date, decimal amount, int type, string? description)
    {
        var normalized = string.Join(' ',
            (description ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{date:yyyy-MM-dd}|{Math.Round(amount, 2)}|{type}|{normalized}");
    }

    // ─── Private Helpers ─────────────────────────────────────────

    private static string ExtractTextFromPdf(Stream pdfStream)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(pdfStream);
        foreach (var page in document.GetPages())
        {
            // page.Text bazen tablo satırlarını atlıyor.
            // GetWords() ile kelimeleri Y koordinatına göre gruplayarak satır oluştur.
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                sb.AppendLine(page.Text);
                continue;
            }

            // Kelimeleri Y koordinatına göre grupla (aynı satırdaki kelimeler ~aynı Y'de)
            var lines = words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                .OrderByDescending(g => g.Key) // PDF'de Y yukarıdan aşağı azalır
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                .ToList();

            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }
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
            return;
        }

        // Aynı onay isteğinde aynı işyeri adı birden fazla kez geçebilir (örn. aynı gün
        // birden fazla ATM çekimi) — henüz SaveChanges çağrılmadığı için DB sorgusu bunu
        // görmez; bu context'te izlenen, henüz kaydedilmemiş bir ekleme varsa UserId+
        // MerchantKeyword unique index çakışmasını önlemek için onu güncelliyoruz.
        var pending = _context.ChangeTracker.Entries<CategoryMapping>()
            .FirstOrDefault(e => e.State == EntityState.Added
                && e.Entity.UserId == userId
                && e.Entity.MerchantKeyword == merchantName);

        if (pending != null)
        {
            pending.Entity.CategoryId = categoryId;
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
