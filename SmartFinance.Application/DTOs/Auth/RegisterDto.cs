using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class RegisterDto
{
    [Required(ErrorMessage = "Ad soyad zorunludur!")]
    [MaxLength(100, ErrorMessage = "Ad soyad en fazla 100 karakter olabilir!")]
    public string FullName {get;set;}=string.Empty;
    [Required(ErrorMessage="Email zorunludur!")]
    [EmailAddress(ErrorMessage ="Geçerli bir email adresi giriniz!")]
    [MaxLength(200, ErrorMessage = "Email en fazla 200 karakter olabilir!")]
    public string Email{get;set;}=string.Empty;
    [Required(ErrorMessage = "Şifre zorunludur!")]
    [MinLength(6,ErrorMessage = "Şifre en az 6 karakter olmalıdır!")]
    [MaxLength(72, ErrorMessage = "Şifre en fazla 72 karakter olabilir!")]
    public string Password{get;set;}=string.Empty;
}