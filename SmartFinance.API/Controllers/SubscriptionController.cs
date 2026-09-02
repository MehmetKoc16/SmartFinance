using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;

    public SubscriptionController(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    /// <summary>
    /// Abonelik durumu ve ucretsiz katman kullanimi.
    /// Uygulama paywall'i, kilitli panelleri ve "3/5 yatirim" gostergelerini
    /// bu yanita gore ciziyor.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
        => Ok(await _subscriptionService.GetStatusAsync(ct));
}
