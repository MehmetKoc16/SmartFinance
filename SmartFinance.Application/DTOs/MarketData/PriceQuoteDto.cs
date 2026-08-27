namespace SmartFinance.Application.DTOs.MarketData;

public class PriceQuoteDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime AsOf { get; set; }

    /// <summary>
    /// Sembolün okunabilir tam adı ("THYAO" -> "Türk Hava Yolları A.O.").
    /// Sağlayıcı zaten dönüyorsa doldurulur; DOLDURMAK İÇİN AYRI BİR DIŞ İSTEK
    /// ATILMAZ — bu yüzden her zaman dolu olacağı varsayılmamalı.
    ///
    /// Kullanıcı yatırım eklerken tam adı elle yazmak zorunda kalmasın diye var.
    /// </summary>
    public string? LongName { get; set; }
}
