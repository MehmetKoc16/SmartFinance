using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace SmartFinance.API.Infrastructure;

public static class ProxyHeaders
{
    public static void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        // Eski ve .NET 10 ag listelerini acikca temizle; sadece loopback'e guven.
#pragma warning disable CS0618, ASPDEPR005
        options.KnownNetworks.Clear();
#pragma warning restore CS0618, ASPDEPR005
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Parse("127.0.0.1"));
        options.KnownProxies.Add(IPAddress.Parse("::1"));
    }
}
