using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Exceptions;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Infrastructure.Services;

namespace SmartFinance.Tests;

public class AuthServiceTests
{
    private (AuthService service,SmartFinanceDbContext context) CreateService()
    {
        var options=new DbContextOptionsBuilder<SmartFinanceDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

        var context = new SmartFinanceDbContext(options);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            {"Jwt:Key","TestGizliAnahtar123456789012345678"},
            {"Jwt:Issuer","TestIssuer"},
            {"Jwt:Audience","TestAudience"},
            {"Jwt:ExpireMinutes","60"}
        }).Build();

        var httpContextAccessor = new HttpContextAccessor();
        var service = new AuthService(context, config, httpContextAccessor);
        return(service,context);
    }

    [Fact]
    public async Task Register_BasariliKayit_TokenDoner()
    {
        var (service, _)=CreateService();
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
        var (service, _)=CreateService();
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
        var (service, _)=CreateService();
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
        var (service, _)=CreateService();

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
        var (service,_)=CreateService();
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
}