using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Auth;

public class RefreshTokenRequestDto
{
    [Required(ErrorMessage = "Refresh token zorunludur!")]
    public string RefreshToken{get;set;}=string.Empty;
}
