using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        var token=await _authService.RegisterAsync(dto);
        return Ok(token);

    } 

    [HttpPost("login")]
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
}