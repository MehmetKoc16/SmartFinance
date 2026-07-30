namespace SmartFinance.Application.DTOs.MarketData;

// Tek noktadaki gosterge degerleri anahtar-deger seklinde tutulur (orn. Stochastic
// icin {"k": 45.2, "d": 40.1}) — boylece tek seri (RSI) ile cok serili (MACD, Ichimoku)
// gostergeler ayni DTO seklini paylasabiliyor, her gosterge icin ayri sinif gerekmiyor.
public class IndicatorPointDto
{
    public DateTime Date { get; set; }
    public Dictionary<string, decimal?> Values { get; set; } = new();
}
