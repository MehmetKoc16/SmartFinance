using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Application.Interfaces;

public interface IPriceProvider
{
    IEnumerable<string> SupportedInvestmentTypes { get; }

    Task<PriceQuoteDto> GetCurrentPriceAsync(string symbol, string investmentType, CancellationToken ct = default);

    Task<IReadOnlyList<PriceBarDto>> GetHistoricalPricesAsync(
        string symbol, string investmentType, DateTime from, DateTime to, CancellationToken ct = default);

    // Sadece hisse senedi saglayicisi (Yahoo) bunu gercekten dolduruyor — digerleri
    // (kripto/altin-doviz/fon) icin sirket temelli istatistikler anlamsiz, o yuzden
    // varsayilan implementasyon null donuyor ve diger saglayicilarin bos metot
    // yazmasina gerek kalmiyor.
    Task<StockStatisticsDto?> GetStatisticsAsync(string symbol, CancellationToken ct = default) =>
        Task.FromResult<StockStatisticsDto?>(null);
}
