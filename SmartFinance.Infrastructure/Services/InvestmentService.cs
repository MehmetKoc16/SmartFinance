using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class InvestmentService : IInvestmentService
{
    private readonly IGenericRepository<Investment> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IMarketDataService _marketDataService;

    public InvestmentService(
        IGenericRepository<Investment> repository,
        SmartFinanceDbContext context,
        ICurrentUserService currentUserService,
        IMarketDataService marketDataService)
    {
        _repository = repository;
        _context = context;
        _currentUserService = currentUserService;
        _marketDataService = marketDataService;
    }

    private int GetUserId() =>
        _currentUserService.UserId;

    public async Task<IEnumerable<InvestmentDto>> GetAllInvestmentsAsync()
    {
        var userId = GetUserId();
        var investments = await _repository.Query().Where(x => x.UserId == userId).ToListAsync();
        return investments
            .OrderByDescending(x => x.CreatedDate)
            .Select(MapToDto);
    }

    public async Task<InvestmentDto?> GetInvestmentByIdAsync(int id)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");
        return MapToDto(investment);
    }

    public async Task<InvestmentDto> CreateInvestmentAsync(CreateInvestmentDto dto)
    {
        var userId = GetUserId();
        var symbol = NormalizeSymbol(dto.Name);

        // Güncel fiyat elle girilmiyor — kayıt öncesi sağlayıcıdan çekiliyor.
        // Sağlayıcı başarısız olursa (yanlış sembol vb.) kayıt oluşturulmaz.
        var quote = await _marketDataService.GetCurrentPriceAsync(symbol, dto.InvestmentType);

        // Aynı semboldan tekrar alım YENİ KAYIT AÇMAZ, mevcut pozisyona eklenir.
        // Aksi halde portföyde aynı hisse birden çok satır olarak görünür ve
        // "bu hissede ne kadar kârdayım" sorusunun tek bir cevabı olmaz.
        var existing = await _repository.Query()
            .Where(x => x.UserId == userId
                     && x.InvestmentType == dto.InvestmentType
                     && x.Name.ToUpper() == symbol)
            .OrderBy(x => x.CreatedDate)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            AddToPosition(existing, dto.PurchasePrice, dto.Quantity);
            existing.CurrentPrice = quote.Price;
            existing.FullName = PickFullName(existing.FullName, quote.LongName, dto.FullName);
            existing.UpdatedDate = DateTime.UtcNow;

            _repository.Update(existing);
            await _context.SaveChangesAsync();

            var mergedDto = MapToDto(existing);
            mergedDto.Merged = true;
            return mergedDto;
        }

        var investment = new Investment
        {
            Name = symbol,
            // Tam ad artık kullanıcıdan istenmiyor: sağlayıcının yanıtında
            // zaten geliyorsa oradan alınıyor, gelmiyorsa boş kalıyor ve
            // arayüz yalnızca sembolü gösteriyor.
            FullName = PickFullName(null, quote.LongName, dto.FullName),
            PurchasePrice = dto.PurchasePrice,
            CurrentPrice = quote.Price,
            Quantity = dto.Quantity,
            InvestmentType = dto.InvestmentType,
            UserId = userId
        };

        await _repository.AddAsync(investment);
        await _context.SaveChangesAsync();

        return MapToDto(investment);
    }

    private static string NormalizeSymbol(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// Mevcut pozisyona alım ekler ve maliyeti AĞIRLIKLI ORTALAMAYA çeker:
    /// (eski toplam maliyet + yeni toplam maliyet) / toplam adet.
    ///
    /// İki fiyatın basit ortalamasını almak yanlış olurdu: 1 adet 100'den,
    /// 9 adet 200'den alındığında gerçek ortalama 190, basit ortalama 150'dir.
    /// </summary>
    private static void AddToPosition(Investment investment, decimal purchasePrice, double quantity)
    {
        var totalQuantity = investment.Quantity + quantity;
        if (totalQuantity <= 0) return;

        var existingCost = investment.PurchasePrice * (decimal)investment.Quantity;
        var addedCost = purchasePrice * (decimal)quantity;

        investment.PurchasePrice = Math.Round((existingCost + addedCost) / (decimal)totalQuantity, 6);
        investment.Quantity = totalQuantity;
    }

    /// Elde olan ad korunur; yoksa sağlayıcının verdiği kullanılır. İstemcinin
    /// gönderdiği ad yalnızca son çare — eski sürümler bu alanı hâlâ yolluyor.
    private static string PickFullName(string? current, string? fromProvider, string? fromClient)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        if (!string.IsNullOrWhiteSpace(fromProvider)) return fromProvider;
        return fromClient?.Trim() ?? string.Empty;
    }

    public async Task UpdateInvestmentAsync(int id, CreateInvestmentDto dto)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");

        var quote = await _marketDataService.GetCurrentPriceAsync(NormalizeSymbol(dto.Name), dto.InvestmentType);

        investment.Name = NormalizeSymbol(dto.Name);
        investment.FullName = PickFullName(null, quote.LongName, dto.FullName);
        investment.PurchasePrice = dto.PurchasePrice;
        investment.CurrentPrice = quote.Price;
        investment.Quantity = dto.Quantity;
        investment.InvestmentType = dto.InvestmentType;
        investment.UpdatedDate = DateTime.UtcNow;

        _repository.Update(investment);
        await _context.SaveChangesAsync();
    }

    public async Task<RefreshPricesResultDto> RefreshPricesAsync()
    {
        var userId = GetUserId();
        var investments = await _repository.Query().Where(x => x.UserId == userId).ToListAsync();

        var result = new RefreshPricesResultDto();

        foreach (var investment in investments)
        {
            try
            {
                var quote = await _marketDataService.GetCurrentPriceAsync(investment.Name, investment.InvestmentType);
                investment.CurrentPrice = quote.Price;
                investment.UpdatedDate = DateTime.UtcNow;
                _repository.Update(investment);
                result.UpdatedCount++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add(new PriceRefreshErrorDto
                {
                    InvestmentId = investment.Id,
                    Name = investment.Name,
                    ErrorMessage = ex.Message,
                });
            }
        }

        await _context.SaveChangesAsync();

        result.Investments = investments.Select(MapToDto).ToList();
        return result;
    }

    public async Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(int id, string range, IEnumerable<string> indicatorKeys)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");

        return await _marketDataService.GetTechnicalAnalysisAsync(investment.Name, investment.InvestmentType, range, indicatorKeys);
    }

    public async Task DeleteInvestmentAsync(int id)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");

        investment.IsDeleted = true;
        investment.UpdatedDate = DateTime.UtcNow;

        _repository.Update(investment);
        await _context.SaveChangesAsync();
    }

    public async Task<PortfolioSummaryDto> GetPortfolioSummaryAsync()
    {
        var userId = GetUserId();
        var investments = await _repository.Query().Where(x => x.UserId == userId).ToListAsync();

        if (!investments.Any())
        {
            return new PortfolioSummaryDto
            {
                TotalInvestmentCount = 0,
                ByType = new List<PortfolioByTypeDto>()
            };
        }

        decimal totalPurchaseValue = investments.Sum(x => x.PurchasePrice * (decimal)x.Quantity);
        decimal totalCurrentValue = investments.Sum(x => x.CurrentPrice * (decimal)x.Quantity);
        decimal totalProfitLoss = totalCurrentValue - totalPurchaseValue;
        decimal totalProfitLossPercentage = totalPurchaseValue == 0
            ? 0
            : Math.Round((totalProfitLoss / totalPurchaseValue) * 100, 2);

        var byType = investments
            .GroupBy(x => x.InvestmentType)
            .Select(g => new PortfolioByTypeDto
            {
                InvestmentType = g.Key,
                TotalCurrentValue = g.Sum(x => x.CurrentPrice * (decimal)x.Quantity),
                ProfitLoss = g.Sum(x => (x.CurrentPrice - x.PurchasePrice) * (decimal)x.Quantity),
                Count = g.Count()
            }).ToList();

        return new PortfolioSummaryDto
        {
            TotalPurchaseValue = totalPurchaseValue,
            TotalCurrentValue = totalCurrentValue,
            TotalProfitLoss = totalProfitLoss,
            TotalProfitLossPercentage = totalProfitLossPercentage,
            TotalInvestmentCount = investments.Count,
            ByType = byType
        };
    }

    private static InvestmentDto MapToDto(Investment investment) => new()
    {
        Id = investment.Id,
        Name = investment.Name,
        FullName = investment.FullName,
        PurchasePrice = investment.PurchasePrice,
        CurrentPrice = investment.CurrentPrice,
        Quantity = investment.Quantity,
        InvestmentType = investment.InvestmentType,
        CreatedDate = investment.CreatedDate
    };
}