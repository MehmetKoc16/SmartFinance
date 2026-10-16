using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartFinance.API.Infrastructure;

public static class ApiRateLimiting
{
    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Login/register brute-force ve spam denemelerine karsi: ayni IP'den dakikada
        // en fazla 5 istek. Bu uc noktalar anonim oldugu icin bolumleme IP bazli.
        options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Dis piyasa servislerini (Yahoo/TEFAS/TCMB/CoinGecko) tetikleyen uc noktalar.
        // Onbellek tekrarli istekleri zaten karsiliyor; bu sinir farkli sembol/aralik
        // kombinasyonlariyla saglayicilarin hiz sinirina takilip IP yasagi yemeyi onler.
        options.AddPolicy("market", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPartitioner.Resolve(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Portfoy fiyat yenilemesi AYRI kotada. "market" kotasini paylasirken grafik
        // araliklari arasinda gezinmek (dakikada 20 istek) onu da dolduruyordu ve
        // yatirimlar ekrani "Fiyatlar guncellenemedi" gosteriyordu (18.09.2026, test
        // kullanicisi). Fiyatlar arka planda toplu tazelendigi icin bu istek
        // cogunlukla onbellekten doner. Politikalarin sayaclari birbirinden bagimsiz.
        options.AddPolicy("prices", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPartitioner.Resolve(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Genel emniyet agi: tum uc noktalar icin kullanici basina dakikada 100 istek.
        // Normal kullanimda asilmayacak kadar yuksek, kacak bir dongu veya kotu niyetli
        // istemcinin sunucuyu yormasini engelleyecek kadar dusuk.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: RateLimitPartitioner.Resolve(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        // CGNAT kullanan cihazlar icin yenileme kotasi giristen bagimsizdir.
        options.AddPolicy("refresh", httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }
}
