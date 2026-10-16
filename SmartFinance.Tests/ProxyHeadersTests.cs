using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartFinance.API.Infrastructure;

namespace SmartFinance.Tests;

public class ProxyHeadersTests
{
    private static TestServer Kur() => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddRouting();
            services.Configure<ForwardedHeadersOptions>(ProxyHeaders.Configure);
            services.AddRateLimiter(ApiRateLimiting.Configure);
        })
        .Configure(app =>
        {
            app.UseForwardedHeaders();
            app.UseRouting();
            app.UseRateLimiter();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/auth", () => "ok").RequireRateLimiting("auth");
                endpoints.MapGet("/refresh", () => "ok").RequireRateLimiting("refresh");
            });
        }));

    private static async Task<int> Iste(TestServer server, string remote, string forwarded, string path = "/auth")
    {
        var response = await server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Request.Headers["X-Forwarded-For"] = forwarded;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        return response.Response.StatusCode;
    }

    // NEDEN: Caddy arkasinda farkli istemciler loopback kotasini paylasmamali.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task LoopbackProxy_FarkliIplerAyriAuthKotasiAlir(string proxy)
    {
        using var server = Kur();
        for (var i = 0; i < 5; i++) Assert.Equal(200, await Iste(server, proxy, "192.0.2.10"));
        Assert.Equal(429, await Iste(server, proxy, "192.0.2.10"));
        Assert.Equal(200, await Iste(server, proxy, "192.0.2.11"));
    }

    // NEDEN: Guvenilmeyen istemci baslik degistirerek IP kotasini asamamali.
    [Fact]
    public async Task LoopbackOlmayanProxy_ForwardedForYokSayilir()
    {
        using var server = Kur();
        for (var i = 0; i < 5; i++) Assert.Equal(200, await Iste(server, "192.0.2.20", $"198.51.100.{i + 1}"));
        Assert.Equal(429, await Iste(server, "192.0.2.20", "198.51.100.99"));
    }

    // NEDEN: CGNAT altindaki oturum yenilemeleri girisin bes istek kotasini paylasmamali.
    [Fact]
    public async Task RefreshPolitikasi_IpBasinaDakikadaOtuzIstek()
    {
        using var server = Kur();
        for (var i = 0; i < 5; i++) Assert.Equal(200, await Iste(server, "127.0.0.1", "192.0.2.10"));
        for (var i = 0; i < 30; i++) Assert.Equal(200, await Iste(server, "127.0.0.1", "192.0.2.10", "/refresh"));
        Assert.Equal(429, await Iste(server, "127.0.0.1", "192.0.2.10", "/refresh"));
        Assert.Equal(200, await Iste(server, "127.0.0.1", "192.0.2.11", "/refresh"));
        var action = typeof(SmartFinance.API.Controllers.AuthController).GetMethod("Refresh")!;
        Assert.Equal("refresh", action.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), true)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().Single().PolicyName);
    }

    // NEDEN: Yalnizca Caddy'nin ekledigi son IP ve HTTPS semasi kabul edilmeli.
    [Fact]
    public async Task ProxyBasliklari_TekAtlamaVeHttpsSemasiniUygular()
    {
        using var server = Kur();
        var response = await server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Path = "/auth";
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.1, 192.0.2.10";
            context.Request.Headers["X-Forwarded-Proto"] = "http, https";
        });
        Assert.Equal(200, response.Response.StatusCode);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), response.Connection.RemoteIpAddress);
        Assert.Equal("https", response.Request.Scheme);
    }
}
