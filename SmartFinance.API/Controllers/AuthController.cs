using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService=authService;
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var token=await _authService.RegisterAsync(dto);
        return Ok(token);

    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var token=await _authService.LoginAsync(dto);
        return Ok(token);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetMe()
    {
        // Token icindeki bilgileri geri yansitmak yerine kullanicinin hala var
        // oldugu dogrulaniyor — silinen hesabin token'i 60 dakika daha gecerli
        // kaliyor ve uygulama giris yapilmis gorunmeye devam ediyordu.
        return Ok(await _authService.GetMeAsync());
    }

    [HttpPut("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        await _authService.ChangePasswordAsync(dto);
        return NoContent();
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
    {
        var result = await _authService.UpdateProfileAsync(dto);
        return Ok(result);
    }

    /// <summary>Erisim (JWT) token'inin suresi doldugunda, kullaniciyi tekrar
    /// login'e dusurmeden yeni bir erisim+refresh token cifti alir. Bilerek
    /// [Authorize] degil — cagrilma amaci zaten suresi gecmis olabilecek bir
    /// erisim token'ini yenilemek.</summary>
    /// <summary>
    /// Hesabi ve kullaniciya ait tum veriyi kalici olarak siler.
    /// Google Play, hesap olusturan uygulamalarda bu ucu zorunlu tutuyor.
    /// </summary>
    [HttpDelete("account")]
    [Authorize]
    // Sifre dogrulayan bir uc: kaba kuvvet denemelerine karsi giris/kayit ile
    // ayni siniri paylasiyor. Web silme sayfasi da bu ucu cagiriyor.
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountDto dto)
    {
        await _authService.DeleteAccountAsync(dto);
        return Ok(new { message = "Hesabınız ve tüm verileriniz kalıcı olarak silindi." });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto)
    {
        var token = await _authService.RefreshTokenAsync(dto.RefreshToken);
        return Ok(token);
    }

    /// <summary>Refresh token'i sunucu tarafinda iptal eder — cikis yapildiginda
    /// cagrilir, boylece cihazda kalan/sizmis bir refresh token artik
    /// kullanilamaz.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequestDto dto)
    {
        await _authService.LogoutAsync(dto.RefreshToken);
        return NoContent();
    }
}
