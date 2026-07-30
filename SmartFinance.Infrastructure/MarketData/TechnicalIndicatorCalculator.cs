using Skender.Stock.Indicators;
using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Infrastructure.MarketData;

public static class TechnicalIndicatorCalculator
{
    // Sadece istenen gostergeler hesaplanir — secilmeyenler icin gereksiz islem/veri yok.
    public static List<IndicatorSeriesDto> Calculate(IReadOnlyList<PriceBarDto> bars, IEnumerable<string> indicatorKeys)
    {
        var wanted = indicatorKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return new List<IndicatorSeriesDto>();

        var quotes = bars
            .OrderBy(b => b.Date)
            .Select(b => new Quote
            {
                Date = b.Date,
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                Volume = b.Volume,
            })
            .ToList();

        return IndicatorCatalog.All
            .Where(def => wanted.Contains(def.Key))
            .Select(def => new IndicatorSeriesDto
            {
                Key = def.Key,
                Points = def.Calculate(quotes),
            })
            .ToList();
    }
}
