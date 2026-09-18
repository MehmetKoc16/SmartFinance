namespace SmartFinance.Application.DTOs.MarketData;

/// Son 12 ay net kar (ana ortaklik payi) ve son bilancodaki ozkaynak, TL.
/// Year/Period: hesabin dayandigi en son donem (Period 2 = 6 aylik).
public record FundamentalSnapshotDto(decimal TtmNetProfit, decimal Equity, int Year, int Period);
