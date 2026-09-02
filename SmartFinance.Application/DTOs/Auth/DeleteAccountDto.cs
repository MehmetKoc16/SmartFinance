using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

/// <summary>
/// Hesap silme isteği. Şifre yeniden isteniyor: silme geri alınamaz ve
/// telefonu açık unutulmuş bir kullanıcının hesabının başkasınca
/// silinmesini engelliyor.
/// </summary>
public class DeleteAccountDto
{
    [Required(ErrorMessage = "Şifre zorunludur!")]
    public string Password { get; set; } = string.Empty;
}
