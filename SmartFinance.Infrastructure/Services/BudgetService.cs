using SmartFinance.Application.Common;
using SmartFinance.Application.DTOs.Budget;
using SmartFinance.Application.Interfaces;
using SmartFinance.Application.Exceptions;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;

namespace SmartFinance.Infrastructure.Services;

public class BudgetService : IBudgetService
{
    private readonly IGenericRepository<Budget> _repository;
    private readonly SmartFinanceDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IEntitlementService _entitlementService;

    public BudgetService(IGenericRepository<Budget> repository, SmartFinanceDbContext context,
        ICurrentUserService currentUserService, IEntitlementService entitlementService)
    {
        _repository = repository;
        _context = context;
        _currentUserService = currentUserService;
        _entitlementService = entitlementService;
    }

    private int GetUserId() =>
        _currentUserService.UserId;

    public async Task<IEnumerable<BudgetDto>> GetAllAsync()
    {
        var userId = GetUserId();
        return await _repository.Query()
            .Where(b => b.UserId == userId)
            .Select(b => new BudgetDto
            {
                Id = b.Id,
                CategoryId = b.CategoryId,
                CategoryName = b.Category.Name,
                Icon = b.Category.Icon,
                Color = b.Category.Color,
                MonthlyLimit = b.MonthlyLimit,
            }).ToListAsync();
    }

    public async Task<BudgetDto> UpsertAsync(CreateBudgetDto dto)
    {
        var userId = GetUserId();

        var category = await _context.Categories
            .FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.UserId == userId);
        if (category == null)
            throw new NotFoundException("Kategori bulunamadı!");

        var existing = await _repository.Query()
            .FirstOrDefaultAsync(b => b.UserId == userId && b.CategoryId == dto.CategoryId);

        // Sinir yalnizca YENI butce eklerken. Mevcut butcenin limitini
        // guncellemek (asagidaki dal) sayiyi artirmadigi icin serbest.
        if (existing == null)
        {
            var mevcutSayi = await _repository.Query().CountAsync(b => b.UserId == userId);
            await _entitlementService.EnsureWithinFreeLimitAsync(
                mevcutSayi, FreeTierLimits.Budgets,
                $"Ücretsiz planda en fazla {FreeTierLimits.Budgets} bütçe tanımlayabilirsiniz. " +
                "Sınırsız bütçe için Premium'a geçin.");
        }

        if (existing != null)
        {
            existing.MonthlyLimit = dto.MonthlyLimit;
            existing.UpdatedDate = DateTime.UtcNow;
            _repository.Update(existing);
            await _context.SaveChangesAsync();

            return new BudgetDto
            {
                Id = existing.Id,
                CategoryId = category.Id,
                CategoryName = category.Name,
                Icon = category.Icon,
                Color = category.Color,
                MonthlyLimit = existing.MonthlyLimit,
            };
        }

        var budget = new Budget
        {
            UserId = userId,
            CategoryId = dto.CategoryId,
            MonthlyLimit = dto.MonthlyLimit,
        };
        await _repository.AddAsync(budget);
        await _context.SaveChangesAsync();

        return new BudgetDto
        {
            Id = budget.Id,
            CategoryId = category.Id,
            CategoryName = category.Name,
            Icon = category.Icon,
            Color = category.Color,
            MonthlyLimit = budget.MonthlyLimit,
        };
    }

    public async Task DeleteAsync(int id)
    {
        var userId = GetUserId();
        var budget = await _repository.GetByIdAsync(id);
        if (budget == null || budget.UserId != userId)
            throw new NotFoundException("Bütçe limiti bulunamadı!");

        budget.IsDeleted = true;
        budget.UpdatedDate = DateTime.UtcNow;
        _repository.Update(budget);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<BudgetStatusDto>> GetStatusAsync(int year, int month)
    {
        var userId = GetUserId();

        var budgets = await _repository.Query()
            .Where(b => b.UserId == userId)
            .Select(b => new
            {
                b.Id,
                b.CategoryId,
                b.MonthlyLimit,
                CategoryName = b.Category.Name,
                b.Category.Icon,
                b.Category.Color,
            })
            .ToListAsync();

        if (budgets.Count == 0) return Enumerable.Empty<BudgetStatusDto>();

        var categoryIds = budgets.Select(b => b.CategoryId).ToList();

        var spentByCategory = await _context.Transactions
            .Where(t => t.UserId == userId
                && t.Type == TransactionType.Expense
                && t.CategoryId != null
                && categoryIds.Contains(t.CategoryId!.Value)
                && t.TransactionDate.Year == year
                && t.TransactionDate.Month == month)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Spent = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Spent);

        return budgets.Select(b =>
        {
            var spent = spentByCategory.GetValueOrDefault(b.CategoryId, 0m);
            return new BudgetStatusDto
            {
                BudgetId = b.Id,
                CategoryId = b.CategoryId,
                CategoryName = b.CategoryName,
                Icon = b.Icon,
                Color = b.Color,
                MonthlyLimit = b.MonthlyLimit,
                Spent = spent,
                Ratio = b.MonthlyLimit > 0 ? spent / b.MonthlyLimit : 0,
                IsOverLimit = spent > b.MonthlyLimit,
            };
        }).ToList();
    }
}
