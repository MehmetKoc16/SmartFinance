using Microsoft.AspNetCore.Mvc;
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
}