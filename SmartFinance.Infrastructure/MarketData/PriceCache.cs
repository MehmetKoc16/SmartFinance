using Microsoft.Extensions.Caching.Memory;
using SmartFinance.Application.DTOs.MarketData;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.MarketData;

public class PriceCache : IPriceCache
{
    private readonly IMemoryCache _cache;

    public PriceCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    // Anahtar üretimi TEK yerde: MarketDataService okurken, PriceRefreshService
    // yazarken aynı biçimi kullanmak zorunda.
    private static string Key(string symbol, string investmentType) =>
        $"price:{investmentType.ToLowerInvariant()}:{symbol.Trim().ToUpperInvariant()}";

    public bool TryGet(string symbol, string investmentType, out PriceQuoteDto? quote)
        => _cache.TryGetValue(Key(symbol, investmentType), out quote) && quote != null;

    public void Set(string symbol, string investmentType, PriceQuoteDto quote, TimeSpan ttl)
        => _cache.Set(Key(symbol, investmentType), quote, ttl);
}
