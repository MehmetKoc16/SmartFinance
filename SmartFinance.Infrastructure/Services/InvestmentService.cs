using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class InvestmentService : IInvestmentService
{
    private readonly IGenericRepository<Investment> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMarketDataService _marketDataService;

    public InvestmentService(
        IGenericRepository<Investment> repository,
        SmartFinanceDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IMarketDataService marketDataService)
    {
        _repository = repository;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _marketDataService = marketDataService;
    }

    private int GetUserId() =>
        int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

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

        // Güncel fiyat elle girilmiyor — kayıt öncesi sağlayıcıdan çekiliyor.
        // Sağlayıcı başarısız olursa (yanlış sembol vb.) kayıt oluşturulmaz.
        var quote = await _marketDataService.GetCurrentPriceAsync(dto.Name, dto.InvestmentType);

        var investment = new Investment
        {
            Name = dto.Name,
            FullName = dto.FullName,
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

    public async Task UpdateInvestmentAsync(int id, CreateInvestmentDto dto)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");

        var quote = await _marketDataService.GetCurrentPriceAsync(dto.Name, dto.InvestmentType);

        investment.Name = dto.Name;
        investment.FullName = dto.FullName;
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

    public async Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(int id, int days = 180)
    {
        var userId = GetUserId();
        var investment = await _repository.GetByIdAsync(id);
        if (investment == null || investment.UserId != userId)
            throw new NotFoundException($"Yatırım bulunamadı. Id: {id}");

        return await _marketDataService.GetTechnicalAnalysisAsync(investment.Name, investment.InvestmentType, days);
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