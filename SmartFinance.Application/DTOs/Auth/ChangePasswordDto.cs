using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class ChangePasswordDto
{
    [Required(ErrorMessage = "Mevcut şifre zorunludur!")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Yeni şifre zorunludur!")]
    [MinLength(6, ErrorMessage = "Yeni şifre en az 6 karakter olmalıdır!")]
    [MaxLength(72, ErrorMessage = "Yeni şifre en fazla 72 karakter olabilir!")]
    public string NewPassword     { get; set; } = string.Empty;
}
