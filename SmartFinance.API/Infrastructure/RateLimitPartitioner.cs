using System.Security.Claims;

namespace SmartFinance.API.Infrastructure;

/// Rate limit kotalarinin hangi anahtara gore ayrildigini belirler.
public static class RateLimitPartitioner
{
    /// Kimligi dogrulanmis istekleri kullanici bazli, anonim istekleri IP bazli
    /// bolumler.
    ///
    /// Kullanici bazli olmasi onemli: ayni agdan (kurumsal NAT, mobil operator
    /// CGNAT, ogrenci yurdu vb.) baglanan farkli kullanicilar tek IP paylasir —
    /// IP bazli bolumlemede bir kullanicinin trafigi digerlerini kilitlerdi.
    ///
    /// NOT: Bunun calismasi icin UseRateLimiter, UseAuthentication'dan SONRA
    /// cagrilmali; aksi halde User.Claims henuz dolmamis olur ve tum kimlik
    /// dogrulanmis kullanicilar ayni "ip:" kotasina duserdi.
    public static string Resolve(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
            return $"user:{userId}";

        return $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
