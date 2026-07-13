using SmartFinance.Application.DTOs.Investment;
using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

public interface IInvestmentService
{
    Task<IEnumerable<InvestmentDto>> GetAllInvestmentsAsync();
    Task<InvestmentDto?> GetInvestmentByIdAsync(int id);
    Task<InvestmentDto> CreateInvestmentAsync(CreateInvestmentDto dto);
    Task UpdateInvestmentAsync(int id, CreateInvestmentDto dto);
    Task DeleteInvestmentAsync(int id);
    Task<PortfolioSummaryDto> GetPortfolioSummaryAsync();
    Task<RefreshPricesResultDto> RefreshPricesAsync();
    Task<TechnicalAnalysisDto> GetTechnicalAnalysisAsync(int id, int days = 180);
}