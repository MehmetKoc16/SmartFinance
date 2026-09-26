using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

/// <summary>Telefonun anlik bildirim (FCM) kaydi.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DeviceTokenController : ControllerBase
{
    private readonly IDeviceTokenService _deviceTokenService;

    public DeviceTokenController(IDeviceTokenService deviceTokenService)
    {
        _deviceTokenService = deviceTokenService;
    }

    /// <summary>Uygulama acilisinda ve token yenilendiginde cagrilir.</summary>
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] DeviceTokenDto dto)
    {
        await _deviceTokenService.RegisterAsync(dto.Token);
        return NoContent();
    }

    /// <summary>Cikis yapilirken cagrilir: o cihaza artik bildirim gitmez.</summary>
    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister([FromBody] DeviceTokenDto dto)
    {
        await _deviceTokenService.UnregisterAsync(dto.Token);
        return NoContent();
    }
}
