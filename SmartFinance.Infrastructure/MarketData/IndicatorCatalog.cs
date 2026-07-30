using Skender.Stock.Indicators;
using SmartFinance.Application.DTOs.MarketData;

namespace SmartFinance.Infrastructure.MarketData;

public record IndicatorDefinition(string Key, string Name, string Category, Func<List<Quote>, List<IndicatorPointDto>> Calculate);

// Tek yerden yonetilen ~33 gosterge kataloğu. Frontend'deki
// lib/core/constants/indicator_catalog.dart bu listeyle (key + isim + kategori)
// birebir senkron tutulmali.
public static class IndicatorCatalog
{
    public static readonly IReadOnlyList<IndicatorDefinition> All = new List<IndicatorDefinition>
    {
        // ── Trend (fiyat grafigine bindirilir) ──────────────────────────
        new("sma20", "SMA (20)", "trend", q => Single(q.GetSma(20), r => r.Date, r => r.Sma)),
        new("sma50", "SMA (50)", "trend", q => Single(q.GetSma(50), r => r.Date, r => r.Sma)),
        new("ema20", "EMA (20)", "trend", q => Single(q.GetEma(20), r => r.Date, r => r.Ema)),
        new("ema50", "EMA (50)", "trend", q => Single(q.GetEma(50), r => r.Date, r => r.Ema)),
        new("bollinger", "Bollinger Bantları", "trend", q => Multi(q.GetBollingerBands(20, 2), r => r.Date, r => new()
        {
            ["upper"] = (decimal?)r.UpperBand,
            ["middle"] = (decimal?)r.Sma,
            ["lower"] = (decimal?)r.LowerBand,
        })),
        new("keltner", "Keltner Kanalları", "trend", q => Multi(q.GetKeltner(20, 2, 10), r => r.Date, r => new()
        {
            ["upper"] = (decimal?)r.UpperBand,
            ["middle"] = (decimal?)r.Centerline,
            ["lower"] = (decimal?)r.LowerBand,
        })),
        new("donchian", "Donchian Kanalları", "trend", q => Multi(q.GetDonchian(20), r => r.Date, r => new()
        {
            ["upper"] = (decimal?)r.UpperBand,
            ["middle"] = (decimal?)r.Centerline,
            ["lower"] = (decimal?)r.LowerBand,
        })),
        new("supertrend", "SuperTrend", "trend", q => Multi(q.GetSuperTrend(10, 3), r => r.Date, r => new()
        {
            ["value"] = (decimal?)r.SuperTrend,
        })),
        new("psar", "Parabolic SAR", "trend", q => Single(q.GetParabolicSar(0.02, 0.2), r => r.Date, r => r.Sar)),
        new("vwap", "VWAP", "trend", q => Single(q.GetVwap(), r => r.Date, r => r.Vwap)),

        // ── Momentum (ayrı osilatör paneli) ──────────────────────────────
        new("rsi", "RSI (14)", "momentum", q => Single(q.GetRsi(14), r => r.Date, r => r.Rsi)),
        new("macd", "MACD (12,26,9)", "momentum", q => Multi(q.GetMacd(12, 26, 9), r => r.Date, r => new()
        {
            ["macd"] = (decimal?)r.Macd,
            ["signal"] = (decimal?)r.Signal,
            ["histogram"] = (decimal?)r.Histogram,
        })),
        new("stoch", "Stochastic Osilatör", "momentum", q => Multi(q.GetStoch(14, 3, 3), r => r.Date, r => new()
        {
            ["k"] = (decimal?)r.K,
            ["d"] = (decimal?)r.D,
        })),
        new("stochrsi", "Stochastic RSI", "momentum", q => Multi(q.GetStochRsi(14, 14, 3, 1), r => r.Date, r => new()
        {
            ["value"] = (decimal?)r.StochRsi,
            ["signal"] = (decimal?)r.Signal,
        })),
        new("cci", "CCI (20)", "momentum", q => Single(q.GetCci(20), r => r.Date, r => r.Cci)),
        new("williamsr", "Williams %R", "momentum", q => Single(q.GetWilliamsR(14), r => r.Date, r => r.WilliamsR)),
        new("roc", "ROC (12)", "momentum", q => Single(q.GetRoc(12), r => r.Date, r => r.Roc)),
        new("ultimate", "Ultimate Osilatör", "momentum", q => Single(q.GetUltimate(7, 14, 28), r => r.Date, r => r.Ultimate)),
        new("awesome", "Awesome Osilatör", "momentum", q => Single(q.GetAwesome(5, 34), r => r.Date, r => r.Oscillator)),
        new("trix", "TRIX (15)", "momentum", q => Single(q.GetTrix(15), r => r.Date, r => r.Trix)),
        new("fisher", "Fisher Transform", "momentum", q => Single(q.GetFisherTransform(10), r => r.Date, r => r.Fisher)),
        new("tsi", "True Strength Index", "momentum", q => Multi(q.GetTsi(25, 13, 7), r => r.Date, r => new()
        {
            ["value"] = (decimal?)r.Tsi,
            ["signal"] = (decimal?)r.Signal,
        })),

        // ── Trend Gücü ────────────────────────────────────────────────
        new("adx", "ADX (14)", "strength", q => Multi(q.GetAdx(14), r => r.Date, r => new()
        {
            ["adx"] = (decimal?)r.Adx,
            ["pdi"] = (decimal?)r.Pdi,
            ["mdi"] = (decimal?)r.Mdi,
        })),
        new("aroon", "Aroon (25)", "strength", q => Multi(q.GetAroon(25), r => r.Date, r => new()
        {
            ["up"] = (decimal?)r.AroonUp,
            ["down"] = (decimal?)r.AroonDown,
        })),
        new("vortex", "Vortex", "strength", q => Multi(q.GetVortex(14), r => r.Date, r => new()
        {
            ["viplus"] = (decimal?)r.Pvi,
            ["viminus"] = (decimal?)r.Nvi,
        })),

        // ── Volatilite ────────────────────────────────────────────────
        new("atr", "ATR (14)", "volatility", q => Single(q.GetAtr(14), r => r.Date, r => r.Atr)),
        new("stddev", "Standart Sapma (20)", "volatility", q => Single(q.GetStdDev(20), r => r.Date, r => r.StdDev)),

        // ── Hacim ─────────────────────────────────────────────────────
        new("obv", "OBV", "volume", q => Single(q.GetObv(), r => r.Date, r => r.Obv)),
        new("mfi", "MFI (14)", "volume", q => Single(q.GetMfi(14), r => r.Date, r => r.Mfi)),
        new("cmf", "Chaikin Para Akışı", "volume", q => Single(q.GetCmf(20), r => r.Date, r => r.Cmf)),
        new("adl", "Birikim/Dağıtım", "volume", q => Single(q.GetAdl(), r => r.Date, r => r.Adl)),
        new("chaikinosc", "Chaikin Osilatörü", "volume", q => Single(q.GetChaikinOsc(3, 10), r => r.Date, r => r.Oscillator)),
        new("forceindex", "Force Index (13)", "volume", q => Single(q.GetForceIndex(13), r => r.Date, r => r.ForceIndex)),
    };

    private static List<IndicatorPointDto> Single<T>(IEnumerable<T> results, Func<T, DateTime> date, Func<T, double?> value) =>
        results.Select(r => new IndicatorPointDto
        {
            Date = date(r),
            Values = new() { ["value"] = (decimal?)value(r) },
        }).ToList();

    private static List<IndicatorPointDto> Multi<T>(IEnumerable<T> results, Func<T, DateTime> date, Func<T, Dictionary<string, decimal?>> values) =>
        results.Select(r => new IndicatorPointDto
        {
            Date = date(r),
            Values = values(r),
        }).ToList();
}
