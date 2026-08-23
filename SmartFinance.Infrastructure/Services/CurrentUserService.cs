using Microsoft.AspNetCore.Http;
using SmartFinance.Application.Interfaces;
using System.Security.Claims;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

/// Istekteki JWT'den oturum acmis kullanicinin kimligini cozer.
///
/// Oncesinde her servis bu satiri kendi icinde tekrarliyordu:
///   int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst(...)!.Value)
/// Bu hem 17 yerde kopyalanmisti, hem de "!" (null-forgiving) operatoru
/// yuzunden claim eksik oldugunda NullReferenceException firlatip 500
/// donduruyordu — dogrusu 401. Artik tek yerde, dogru hata tipiyle cozuluyor.
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    // Kimligin cozulememesinin uc ayri sebebi var (istek disi cagri, claim yok,
    // claim sayiya cevrilemiyor) ama kullanici acisindan sonuc ayni: oturum
    // gecerli degil. Tek mesajda toplandi.
    private const string OturumGecersiz = "Oturum bilgisi geçersiz, lütfen tekrar giriş yapın.";

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public int UserId
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext
                ?? throw new UnauthorizedException(OturumGecersiz);

            var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)
                ?? throw new UnauthorizedException(OturumGecersiz);

            if (!int.TryParse(userIdClaim.Value, out var userId))
                throw new UnauthorizedException(OturumGecersiz);

            return userId;
        }
    }
}
