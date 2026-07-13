using Skender.Stock.Indicators;
using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Infrastructure.MarketData;

public static class TechnicalIndicatorCalculator
{
    public static TechnicalAnalysisDto Calculate(string symbol, string investmentType, IReadOnlyList<PriceBarDto> bars)
    {
        var orderedBars = bars.OrderBy(b => b.Date).ToList();

        var quotes = orderedBars
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

        var rsiResults = quotes.GetRsi(14).ToList();
        var macdResults = quotes.GetMacd(12, 26, 9).ToList();
        var bollingerResults = quotes.GetBollingerBands(20, 2).ToList();

        return new TechnicalAnalysisDto
        {
            Symbol = symbol,
            InvestmentType = investmentType,
            PriceBars = orderedBars,
            Rsi = rsiResults.Select(r => new IndicatorPointDto
            {
                Date = r.Date,
                Value = (decimal?)r.Rsi,
            }).ToList(),
            Macd = new MacdResultDto
            {
                Points = macdResults.Select(m => new MacdPointDto
                {
                    Date = m.Date,
                    Macd = (decimal?)m.Macd,
                    Signal = (decimal?)m.Signal,
                    Histogram = (decimal?)m.Histogram,
                }).ToList(),
            },
            BollingerBands = new BollingerBandsResultDto
            {
                Points = bollingerResults.Select(b => new BollingerBandPointDto
                {
                    Date = b.Date,
                    UpperBand = (decimal?)b.UpperBand,
                    MiddleBand = (decimal?)b.Sma,
                    LowerBand = (decimal?)b.LowerBand,
                }).ToList(),
            },
        };
    }
}
