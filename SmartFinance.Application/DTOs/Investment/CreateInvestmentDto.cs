using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Investment;

public class CreateInvestmentDto{
    [Required(ErrorMessage = "Sembol zorunludur!")]
    [MaxLength(50, ErrorMessage = "Sembol en fazla 50 karakter olabilir!")]
    public string Name{get;set;}=string.Empty;

    /// <summary>
    /// İsteğe bağlı. Arayüz artık bu alanı sormuyor — tam ad, fiyat sorgusunun
    /// yanıtından otomatik dolduruluyor. Eski istemciler hâlâ gönderebildiği
    /// için alan korunuyor.
    /// </summary>
    [MaxLength(200, ErrorMessage = "Tam ad en fazla 200 karakter olabilir!")]
    public string FullName{get;set;}=string.Empty;

    [Range(0.01, double.MaxValue, ErrorMessage = "Alış fiyatı 0'dan büyük olmalıdır!")]
    public decimal PurchasePrice{get;set;}

    // Alt sinir kripto icin 1 satoshi (0,00000001 BTC). Onceki 0,0001 siniri
    // hisse dusunularek konmustu; BTC'de 0,0001 bugunku kurla ~370 TL ediyor,
    // yani bundan az kriptosu olan kullanici pozisyonunu hic ekleyemiyordu.
    [Range(0.00000001, double.MaxValue, ErrorMessage = "Miktar 0'dan büyük olmalıdır!")]
    public double Quantity{get;set;}

    [Required(ErrorMessage = "Yatırım tipi zorunludur!")]
    [MaxLength(20, ErrorMessage = "Yatırım tipi en fazla 20 karakter olabilir!")]
    public string InvestmentType{get;set;}=string.Empty;
}