using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class InvestmentService : IInvestmentService
{
    private readonly IGenericRepository<Investment> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InvestmentService(
        IGenericRepository<Investment> repository,
        SmartFinanceDbContext context,
        IHttpContextAccessor httpContextAccessor)
    {
        _repository = repository;
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    private int GetUserId() =>
        int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    public async Task<IEnumerable<InvestmentDto>> GetAllInvestmentsAsync()
    {
        var userId = GetUserId();
        var investments = await _repository.GetAllAsync();
        return investments
            .Where(x => x.UserId == userId)
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

        var investment = new Investment
        {
            Name = dto.Name,
            FullName = dto.FullName,
            PurchasePrice = dto.PurchasePrice,
            CurrentPrice = dto.CurrentPrice,
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

        investment.Name = dto.Name;
        investment.FullName = dto.FullName;
        investment.PurchasePrice = dto.PurchasePrice;
        investment.CurrentPrice = dto.CurrentPrice;
        investment.Quantity = dto.Quantity;
        investment.InvestmentType = dto.InvestmentType;
        investment.UpdatedDate = DateTime.UtcNow;

        _repository.Update(investment);
        await _context.SaveChangesAsync();
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
        var allInvestments = await _repository.GetAllAsync();
        var investments = allInvestments.Where(x => x.UserId == userId).ToList();

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