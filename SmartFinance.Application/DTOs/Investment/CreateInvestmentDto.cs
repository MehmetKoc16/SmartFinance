using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Investment;

public class CreateInvestmentDto{
    [Required(ErrorMessage = "Sembol zorunludur!")]
    [MaxLength(50, ErrorMessage = "Sembol en fazla 50 karakter olabilir!")]
    public string Name{get;set;}=string.Empty;

    [MaxLength(200, ErrorMessage = "Tam ad en fazla 200 karakter olabilir!")]
    public string FullName{get;set;}=string.Empty;

    [Range(0.01, double.MaxValue, ErrorMessage = "Alış fiyatı 0'dan büyük olmalıdır!")]
    public decimal PurchasePrice{get;set;}

    // Sadece "fund" tipi için kullanılıyor — TEFAS otomatik fiyat çekimi şu an çalışmadığından
    // geçici olarak elle giriliyor. Diğer tiplerde bu alan yok sayılır, fiyat sağlayıcıdan çekilir.
    [Range(0, double.MaxValue, ErrorMessage = "Güncel fiyat negatif olamaz!")]
    public decimal CurrentPrice{get;set;}

    [Range(0.0001, double.MaxValue, ErrorMessage = "Miktar 0'dan büyük olmalıdır!")]
    public double Quantity{get;set;}

    [Required(ErrorMessage = "Yatırım tipi zorunludur!")]
    [MaxLength(20, ErrorMessage = "Yatırım tipi en fazla 20 karakter olabilir!")]
    public string InvestmentType{get;set;}=string.Empty;
}