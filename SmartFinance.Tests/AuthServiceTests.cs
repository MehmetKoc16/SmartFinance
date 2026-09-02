using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class AuthServiceTests
{
    private (AuthService service,SmartFinanceDbContext context,HttpContextAccessor httpContextAccessor) CreateService()
    {
        var options=new DbContextOptionsBuilder<SmartFinanceDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

        var context = new SmartFinanceDbContext(options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            {"Jwt:Key","TestGizliAnahtar123456789012345678"},
            {"Jwt:Issuer","TestIssuer"},
            {"Jwt:Audience","TestAudience"},
            {"Jwt:ExpireMinutes","60"},
            {"Jwt:RefreshTokenExpireDays","30"}
        }).Build();

        var httpContextAccessor = new HttpContextAccessor();
        var service = new AuthService(context, config, new CurrentUserService(httpContextAccessor),
            new FakeEmailSender(), NullLogger<AuthService>.Instance);
        return(service,context,httpContextAccessor);
    }

    // UpdateProfile gibi HttpContext'ten UserId okuyan metotlar icin, o an
    // "giris yapmis" kullaniciyi taklit eder.
    private static void SetCurrentUser(HttpContextAccessor accessor, int userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) });
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    /// Ad ve e-postadaki bastaki/sondaki bosluklar kirpilmali: kirpilmazsa
    /// "Mehmet Koc " gibi bir ad e-postada "Merhaba Mehmet Koc ," seklinde
    /// gorunuyordu (canli e-postada goruldu).
    [Fact]
    public async Task Register_AdVeEpostadakiBosluklariKirpar()
    {
        var (service, context, _) = CreateService();

        await service.RegisterAsync(new RegisterDto
        {
            FullName = "  Mehmet Koç  ",
            Email = "  bosluklu@test.com  ",
            Password = "Sifre123!",
        });

        var user = context.Users.Single();
        Assert.Equal("Mehmet Koç", user.FullName);
        Assert.Equal("bosluklu@test.com", user.Email);
    }

    [Fact]
    public async Task Register_BasariliKayit_TokenDoner()
    {
        var (service, _, _)=CreateService();
        var dto = new RegisterDto
        {
            FullName="Test Kullanıcı",
            Email="test@test.com",
            Password="Sifre123!"
        };

        var result = await service.RegisterAsync(dto);

        Assert.NotNull(result.Token);
        Assert.True(result.Token.Length>0);

    }

    [Fact]
    public async Task Register_AyniEmail_BadRequestHatasiFireder()
    {
        var (service, _, _)=CreateService();
        var dto=new RegisterDto{
                FullName = "Test",
            Email = "tekrar@test.com",
            Password = "Sifre123!"
        };
        await service.RegisterAsync(dto);

        await Assert.ThrowsAsync<BadRequestException>(
            ()=>service.RegisterAsync(dto)
        );
    }

    [Fact]
    public async Task Login_DogruBilgi_TokenDoner()
    {
        var (service, _, _)=CreateService();
        await service.RegisterAsync(new RegisterDto{
            FullName = "Test",
            Email = "login@test.com",
            Password = "Sifre123!"
        });

        var result=await service.LoginAsync(new LoginDto{
            Email = "login@test.com",
            Password = "Sifre123!"
        });

        Assert.NotNull(result.Token);
    }

    [Fact]
    public async Task Login_YanlisEmail_UnauthorizedHatasiFireder()
    {
        var (service, _, _)=CreateService();

        await Assert.ThrowsAsync<UnauthorizedException>(
            ()=>service.LoginAsync(new LoginDto{
                 Email = "yok@test.com",
                Password = "Sifre123!"
            })
        );
    }

    [Fact]
    public async Task Login_YanlisSifre_UnauthorizedHatasiFireder()
    {
        var (service,_, _)=CreateService();
        await service.RegisterAsync(new RegisterDto{
            FullName = "Test",
            Email = "sifre@test.com",
            Password = "DogruSifre123!"
        });

        await Assert.ThrowsAsync<UnauthorizedException>(
            ()=>service.LoginAsync(new LoginDto{
                Email = "sifre@test.com",
                Password = "YanlisSifre!"
            })
        );
    }

    [Fact]
    public async Task RefreshToken_GecerliToken_YeniTokenCiftiDonerVeEskisiIptalOlur()
    {
        var (service,context,_)=CreateService();
        var login = await service.RegisterAsync(new RegisterDto{
            FullName = "Test",
            Email = "refresh@test.com",
            Password = "Sifre123!"
        });

        var result = await service.RefreshTokenAsync(login.RefreshToken);

        Assert.NotNull(result.Token);
        Assert.NotEqual(login.RefreshToken, result.RefreshToken);

        var eskiToken = await context.RefreshTokens.FirstAsync(rt => rt.Token == login.RefreshToken);
        Assert.NotNull(eskiToken.RevokedAt);
        Assert.False(eskiToken.IsActive);
    }

    [Fact]
    public async Task RefreshToken_IptalEdilmisTokenTekrarKullanilirsa_UnauthorizedFireder()
    {
        // Rotasyonun asil amaci: calinmis bir refresh token bir kez kullanildiktan
        // sonra tekrar kullanilmaya calisilirsa reddedilmeli (replay saldirisi savunmasi).
        var (service,_,_)=CreateService();
        var login = await service.RegisterAsync(new RegisterDto{
            FullName = "Test",
            Email = "rotasyon@test.com",
            Password = "Sifre123!"
        });

        await service.RefreshTokenAsync(login.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedException>(
            ()=>service.RefreshTokenAsync(login.RefreshToken)
        );
    }

    [Fact]
    public async Task RefreshToken_GecersizToken_UnauthorizedFireder()
    {
        var (service,_,_)=CreateService();

        await Assert.ThrowsAsync<UnauthorizedException>(
            ()=>service.RefreshTokenAsync("olmayan-bir-token")
        );
    }

    [Fact]
    public async Task Logout_GecerliToken_SonrakiRefreshDenemesiReddedilir()
    {
        var (service,_,_)=CreateService();
        var login = await service.RegisterAsync(new RegisterDto{
            FullName = "Test",
            Email = "logout@test.com",
            Password = "Sifre123!"
        });

        await service.LogoutAsync(login.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedException>(
            ()=>service.RefreshTokenAsync(login.RefreshToken)
        );
    }

    [Fact]
    public async Task Logout_OlmayanToken_SessizceBasarili()
    {
        var (service,_,_)=CreateService();

        var ex = await Record.ExceptionAsync(() => service.LogoutAsync("hic-var-olmamis-token"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task UpdateProfile_GecerliBilgi_AdVeEmailGuncellenir()
    {
        var (service,context,accessor)=CreateService();
        await service.RegisterAsync(new RegisterDto{
            FullName = "Eski Ad",
            Email = "eski@test.com",
            Password = "Sifre123!"
        });
        var user = await context.Users.FirstAsync(u => u.Email == "eski@test.com");
        SetCurrentUser(accessor, user.Id);

        var result = await service.UpdateProfileAsync(new UpdateProfileDto{
            FullName = "Yeni Ad",
            Email = "yeni@test.com"
        });

        var guncel = await context.Users.FindAsync(user.Id);
        Assert.Equal("Yeni Ad", guncel!.FullName);
        Assert.Equal("yeni@test.com", guncel.Email);
    }

    [Fact]
    public async Task UpdateProfile_BaskaKullaniciyaAitEmail_BadRequestFireder()
    {
        var (service,context,accessor)=CreateService();
        await service.RegisterAsync(new RegisterDto{
            FullName = "Kullanici Bir",
            Email = "biri@test.com",
            Password = "Sifre123!"
        });
        await service.RegisterAsync(new RegisterDto{
            FullName = "Kullanici Iki",
            Email = "ikinci@test.com",
            Password = "Sifre123!"
        });
        var user2 = await context.Users.FirstAsync(u => u.Email == "ikinci@test.com");
        SetCurrentUser(accessor, user2.Id);

        await Assert.ThrowsAsync<BadRequestException>(
            ()=>service.UpdateProfileAsync(new UpdateProfileDto{
                FullName = "Kullanici Iki",
                Email = "biri@test.com"
            })
        );
    }
}
