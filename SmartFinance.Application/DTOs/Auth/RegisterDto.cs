using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class RegisterDto
{
    [Required(ErrorMessage = "Ad soyad zorunludur!")]
    public string FullName {get;set;}=string.Empty;
    [Required(ErrorMessage="Email zorunludur!")]
    [EmailAddress(ErrorMessage ="Geçerli bir email adresi giriniz!")]
    public string Email{get;set;}=string.Empty;
    [Required(ErrorMessage = "Şifre zorunludur!")]
    [MinLength(6,ErrorMessage = "Şifre en az 6 karakter olmalıdır!")]
    public string Password{get;set;}=string.Empty;
}