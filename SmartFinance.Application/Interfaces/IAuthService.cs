using SmartFinance.Application.DTOs.Auth;

namespace SmartFinance.Application.Interfaces;

public interface IAuthService{
    Task<TokenDto> RegisterAsync(RegisterDto dto);
    Task<TokenDto> LoginAsync(LoginDto dto);
    Task ChangePasswordAsync(ChangePasswordDto dto);
    Task<TokenDto> RefreshTokenAsync(string refreshToken);
    Task LogoutAsync(string refreshToken);
}