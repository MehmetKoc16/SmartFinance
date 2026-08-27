using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// Dış servislerden (TEFAS, Yahoo Finance) çekilen günlük fiyat geçmişi.
///
/// Neden kendi tablomuzda saklıyoruz:
/// - TEFAS tek istekte en fazla 1 aylık veri veriyor ve IP başına dakikada ~6
///   istekle sınırlıyor; 6 aylık grafik istek anında çekildiğinde ~90 saniye
///   sürüyordu ve bu kota tüm kullanıcılar arasında paylaşılıyordu.
/// - Yahoo'nun sınırı belgelenmemiş ve IP bazlı; grafik isteklerini oraya
///   taşımak, kullanıcı sayısıyla büyüyen kontrolsüz bir yük demekti.
///
/// Veri kullanıcıya değil sembole ait: aynı sembolü tutan tüm kullanıcılar
/// bu kayıtları paylaşır.
/// </summary>
public class PriceHistory : BaseEntity
{
    /// Sembol (TEFAS fon kodu "AFA" veya hisse kodu "THYAO").
    /// Büyük harfe normalize edilerek saklanır.
    public string Symbol { get; set; } = string.Empty;

    /// "fund", "stock" vb. Aynı kod farklı piyasalarda çakışabileceği için
    /// tekillik sembol + tip + tarih üzerinden kurulur.
    public string InvestmentType { get; set; } = string.Empty;

    /// Fiyatın ait olduğu gün (saat bilgisi taşımaz).
    public DateTime Date { get; set; }

    /// Kapanış / birim pay fiyatı. TEFAS 6 ondalık basamakla yayınlıyor.
    public decimal Close { get; set; }

    /// Gün içi açılış / en yüksek / en düşük ve hacim — hisse senedinde anlamlı.
    /// TEFAS yalnızca tek NAV fiyatı yayınladığı için fonlarda bu alanlar
    /// Close ile aynı değeri taşır, hacim 0'dır.
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Volume { get; set; }
}
