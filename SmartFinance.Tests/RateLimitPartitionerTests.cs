using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartFinance.API.Infrastructure;

namespace SmartFinance.Tests;

public class RateLimitPartitionerTests
{
    private static HttpContext Context(string? userId, string? ip)
    {
        var context = new DefaultHttpContext();
        if (userId != null)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)]);
            context.User = new ClaimsPrincipal(identity);
        }
        if (ip != null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public void KimlikDogrulanmis_KullaniciBazliBolumler()
    {
        var key = RateLimitPartitioner.Resolve(Context(userId: "42", ip: "1.2.3.4"));

        Assert.Equal("user:42", key);
    }

    [Fact]
    public void Anonim_IpBazliBolumler()
    {
        var key = RateLimitPartitioner.Resolve(Context(userId: null, ip: "1.2.3.4"));

        Assert.Equal("ip:1.2.3.4", key);
    }

    /// En kritik davranis: ayni agdan (kurumsal NAT / mobil operator CGNAT)
    /// baglanan iki kullanici ayni IP'yi paylassa bile ayri kotalara dusmeli,
    /// yoksa bir kullanicinin trafigi digerini kilitler.
    [Fact]
    public void AyniIpFarkliKullanicilar_AyriKotalaraDuser()
    {
        var birinci = RateLimitPartitioner.Resolve(Context(userId: "42", ip: "1.2.3.4"));
        var ikinci = RateLimitPartitioner.Resolve(Context(userId: "99", ip: "1.2.3.4"));

        Assert.NotEqual(birinci, ikinci);
    }

    /// Ayni kullanici farkli cihaz/agdan girse de tek kota kullanmali —
    /// aksi halde IP degistirerek limit asilabilirdi.
    [Fact]
    public void AyniKullaniciFarkliIp_AyniKotayiPaylasir()
    {
        var evden = RateLimitPartitioner.Resolve(Context(userId: "42", ip: "1.2.3.4"));
        var mobilden = RateLimitPartitioner.Resolve(Context(userId: "42", ip: "9.8.7.6"));

        Assert.Equal(evden, mobilden);
    }

    [Fact]
    public void IpBilinmiyorsa_Cokmez()
    {
        var key = RateLimitPartitioner.Resolve(Context(userId: null, ip: null));

        Assert.Equal("ip:unknown", key);
    }

    /// Kullanici ve IP anahtarlari birbirine karismamali.
    [Fact]
    public void KullaniciVeIpAnahtarlari_Cakismaz()
    {
        var kullanici = RateLimitPartitioner.Resolve(Context(userId: "42", ip: null));
        var ip = RateLimitPartitioner.Resolve(Context(userId: null, ip: "42.42.42.42"));

        Assert.NotEqual(kullanici, ip);
    }
}
