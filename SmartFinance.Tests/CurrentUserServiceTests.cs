using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Services;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Tests;

public class CurrentUserServiceTest
{
    [Fact]
    public void UserId_HttpContextYok_UnauthorizedFirlatir()
    {
        var httpContextAccessor = new HttpContextAccessor{};
        var currentUserService = new CurrentUserService(httpContextAccessor);

        Assert.Throws<UnauthorizedException>(() => currentUserService.UserId);

    }
     [Fact]
    public void UserId_ClaimYok_UnauthorizedFirlatir()
    {
       var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new       ClaimsPrincipal(new ClaimsIdentity()) }
        };
        var currentUserService = new CurrentUserService(httpContextAccessor);

        Assert.Throws<UnauthorizedException>(() => currentUserService.UserId);

    }
      [Fact]
    public void UserId_ClaimSayiDegil_UnauthorizedFirlatir()
    {
        var identity = new ClaimsIdentity(new[] { new Claim     (ClaimTypes.NameIdentifier, "abc") });
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new       ClaimsPrincipal(identity) }
        };
        var currentUserService = new CurrentUserService(httpContextAccessor);

        Assert.Throws<UnauthorizedException>(() => currentUserService.UserId);

    }
}