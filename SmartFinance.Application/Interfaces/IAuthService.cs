using SmartFinance.Application.DTOs.Auth;

namespace SmartFinance.Application.Interfaces;

public interface IAuthService{
    Task<TokenDto> RegisterAsync(RegisterDto dto);
    Task<TokenDto> LoginAsync(LoginDto dto);
    Task ChangePasswordAsync(ChangePasswordDto dto);
    Task<TokenDto> RefreshTokenAsync(string refreshToken);
    Task LogoutAsync(string refreshToken);
    Task<object> UpdateProfileAsync(UpdateProfileDto dto);

    /// Oturumdaki kullaniciyi doner. Yalnizca token icindeki bilgilere
    /// guvenmez, kullanicinin HALA var oldugunu dogrular.
    Task<object> GetMeAsync();

    /// Hesabi ve kullaniciya ait TUM veriyi kalici olarak siler.
    /// Google Play, hesap olusturan uygulamalarda bunu zorunlu tutuyor.
    Task DeleteAccountAsync(DeleteAccountDto dto);
}