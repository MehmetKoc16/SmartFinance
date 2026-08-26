using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

/// <summary>
/// TEFAS'tan cekilen fon NAV (birim pay fiyati) gecmisi.
///
/// Neden kendi tablomuzda saklıyoruz: TEFAS API'si tek istekte en fazla 1 aylik
/// veri veriyor ve IP basina dakikada ~6 istekle sinirliyor. Her kullanici
/// isteginde oradan cekmek 6 aylik grafik icin ~90 saniye suruyordu ve bu sure
/// tum kullanicilar arasinda paylasilan bir kotadan yeniyordu. Veriyi bir kez
/// alip sakladigimizda gecelik senkron fon basina gunde 1 istege dusuyor,
/// kullanici ise hic beklemiyor.
///
/// Kullaniciya ozel degil, fona ozel bir veri: ayni fonu tutan tum kullanicilar
/// bu kayitlari paylasir.
/// </summary>
public class FundPriceHistory : BaseEntity
{
    /// TEFAS fon kodu (ornek: "AFA"). Buyuk harfe normalize edilerek saklanir.
    public string FundCode { get; set; } = string.Empty;

    /// NAV'in ait oldugu gun (saat bilgisi tasimaz).
    public DateTime Date { get; set; }

    /// Birim pay fiyati. TEFAS 6 ondalik basamakla yayinliyor.
    public decimal Price { get; set; }
}
