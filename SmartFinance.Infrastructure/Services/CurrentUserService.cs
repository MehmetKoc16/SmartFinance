using Microsoft.AspNetCore.Http;
using SmartFinance.Application.Interfaces;
using System.Security.Claims;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor=httpContextAccessor;
    }

    public int UserId
    {
        get
        {
            if(_httpContextAccessor.HttpContext==null)
            {
                 throw new UnauthorizedException("Oturum bilgisi geçersiz, lütfen tekrar giriş yapın");
                 
            }
            var userIdClaim =_httpContextAccessor.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier);
            
                if(userIdClaim == null)
                {
                     throw new UnauthorizedException("Giriş yapmalısın");
                }
                
                
                    bool kontrol=int.TryParse(userIdClaim .Value, out var sonuc);
                    if(kontrol)
                    {
                        return sonuc ;
                    }
                       throw new UnauthorizedException("giriş yapmalısın");
                
            

        }
    }
}