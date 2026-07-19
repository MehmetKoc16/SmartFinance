using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class UpdateProfileDto
{
    [Required(ErrorMessage = "Ad soyad zorunludur!")]
    [MaxLength(100, ErrorMessage = "Ad soyad en fazla 100 karakter olabilir!")]
    public string FullName{get;set;}=string.Empty;

    [Required(ErrorMessage = "Email zorunludur!")]
    [EmailAddress(ErrorMessage = "Geçerli bir email adresi giriniz!")]
    [MaxLength(200, ErrorMessage = "Email en fazla 200 karakter olabilir!")]
    public string Email{get;set;}=string.Empty;
}
