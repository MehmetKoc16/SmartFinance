using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    public static readonly EventId GirisBasariliEvent = new(4101, "GirisBasarili");
    public static readonly EventId GirisBasarisizEvent = new(4102, "GirisBasarisiz");
    public static readonly EventId YenilemeBasarisizEvent = new(4103, "YenilemeBasarisiz");

    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    // Logger istege bagli: testler ve eski kod kontrolcuyu tek parametreyle
    // kuruyor. DI'da ILogger<T> her zaman kayitli oldugu icin gercek logger gelir.
    public AuthController(IAuthService authService, ILogger<AuthController>? logger = null)
    {
        _authService=authService;
        _logger = logger ?? NullLogger<AuthController>.Instance;
    }

    // ProxyHeaders gercek istemci IP'sini RemoteIpAddress'e yerlestiriyor.
    private string ClientIp =>
        HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "bilinmiyor";

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
        TokenDto token;
        try
        {
            token=await _authService.LoginAsync(dto);
        }
        catch (UnauthorizedException)
        {
            // Ihlal tespiti icin IP yazilir; e-posta (kisisel veri) YAZILMAZ.
            // throw; ile ayni istisna ayni yigin iziyle ExceptionMiddleware'e
            // gider, cevap (401 + govde) degismez.
            _logger.LogWarning(GirisBasarisizEvent, "GirisBasarisiz: IP={ClientIp}", ClientIp);
            throw;
        }

        var userId = UserIdFromToken(token.Token);
        if (userId != null)
            _logger.LogInformation(GirisBasariliEvent, "GirisBasarili: UserId={UserId} IP={ClientIp}", userId, ClientIp);
        else
            _logger.LogInformation(GirisBasariliEvent, "GirisBasarili: IP={ClientIp}", ClientIp);
        return Ok(token);
    }

    /// TokenDto'da kullanici kimligi yok (DTO'ya dokunulmuyor); yeni uretilen
    /// JWT'nin payload'indan okunur. Imza burada dogrulanmaz: token az once
    /// servis tarafindan uretildi, deger yalnizca loga yazilir. JwtSecurityTokenHandler
    /// ClaimTypes.NameIdentifier'i "nameid" olarak yazar; digerleri yedek.
    private static string? UserIdFromToken(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var parsed = new JsonWebToken(jwt);
            foreach (var claimType in new[] { "nameid", ClaimTypes.NameIdentifier, "sub" })
            {
                if (parsed.TryGetPayloadValue<string>(claimType, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch (Exception)
        {
            // Bicimi bozuk token (SecurityTokenMalformedException vb.): giris
            // basarili, yalnizca kimlik loga yazilamaz; cevap etkilenmemeli.
        }
        return null;
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
        var token = await _authService.ChangePasswordAsync(dto);
        return Ok(token);
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
    /// Sifre sifirlama baglantisi gonderir.
    ///
    /// E-posta kayitli olmasa bile 200 doner. Aksi halde bu uc bir "hesap var
    /// mi" sorgulama aracina donusurdu: saldirgan e-posta listesini deneyip
    /// hangilerinin kayitli oldugunu ogrenebilirdi.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto, CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(dto, ct);
        return Ok(new { message = "Bu adres kayıtlıysa şifre sıfırlama bağlantısı gönderildi. Lütfen e-postanızı kontrol edin." });
    }

    /// <summary>
    /// Sifirlama baglantisindaki token ile yeni sifreyi belirler.
    /// </summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto, CancellationToken ct)
    {
        await _authService.ResetPasswordAsync(dto, ct);
        return Ok(new { message = "Şifreniz güncellendi. Yeni şifrenizle giriş yapabilirsiniz." });
    }

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
    [EnableRateLimiting("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto)
    {
        try
        {
            var token = await _authService.RefreshTokenAsync(dto.RefreshToken);
            return Ok(token);
        }
        catch (UnauthorizedException)
        {
            // Refresh token degeri bir sir: loga yalnizca IP yazilir.
            _logger.LogWarning(YenilemeBasarisizEvent, "YenilemeBasarisiz: IP={ClientIp}", ClientIp);
            throw;
        }
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
