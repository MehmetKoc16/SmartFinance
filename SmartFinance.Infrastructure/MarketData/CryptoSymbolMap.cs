namespace SmartFinance.Infrastructure.MarketData;

public static class CryptoSymbolMap
{
    public static readonly Dictionary<string, string> TickerToCoinGeckoId = new(StringComparer.OrdinalIgnoreCase)
    {
        { "BTC", "bitcoin" },
        { "ETH", "ethereum" },
        { "USDT", "tether" },
        { "BNB", "binancecoin" },
        { "SOL", "solana" },
        { "XRP", "ripple" },
        { "ADA", "cardano" },
        { "DOGE", "dogecoin" },
        { "AVAX", "avalanche-2" },
        { "DOT", "polkadot" },
        { "MATIC", "matic-network" },
        { "LTC", "litecoin" },
        { "LINK", "chainlink" },
        { "TRX", "tron" },
        { "ATOM", "cosmos" },
        { "USDC", "usd-coin" },
        { "SHIB", "shiba-inu" },
    };
}
