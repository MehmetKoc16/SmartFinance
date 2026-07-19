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
    public IActionResult GetMe()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email  = User.FindFirst(ClaimTypes.Email)?.Value;
        var name   = User.FindFirst(ClaimTypes.Name)?.Value;
        return Ok(new { id = userId, email, fullName = name });
    }

    [HttpPut("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        await _authService.ChangePasswordAsync(dto);
        return NoContent();
    }

    /// <summary>Erisim (JWT) token'inin suresi doldugunda, kullaniciyi tekrar
    /// login'e dusurmeden yeni bir erisim+refresh token cifti alir. Bilerek
    /// [Authorize] degil — cagrilma amaci zaten suresi gecmis olabilecek bir
    /// erisim token'ini yenilemek.</summary>
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