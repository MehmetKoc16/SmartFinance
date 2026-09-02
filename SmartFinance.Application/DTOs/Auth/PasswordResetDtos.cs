using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class ForgotPasswordDto
{
    [Required(ErrorMessage = "E-posta zorunludur!")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi giriniz!")]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordDto
{
    [Required(ErrorMessage = "Sıfırlama kodu zorunludur!")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Yeni şifre zorunludur!")]
    [MinLength(6, ErrorMessage = "Yeni şifre en az 6 karakter olmalıdır!")]
    public string NewPassword { get; set; } = string.Empty;
}
