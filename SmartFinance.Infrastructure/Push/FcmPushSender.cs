using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.Push;

/// <summary>
/// Firebase Cloud Messaging (HTTP v1) ile gonderim. Hizmet hesabi anahtari
/// sunucuda dosyada durur (depoya GIRMEZ); yolu Firebase:CredentialsPath.
/// </summary>
public sealed class FcmPushSender : IPushSender
{
    // Android'de bildirim kanali: uygulama tarafinda ayni kimlikle olusturuluyor.
    public const string AndroidChannelId = "genel";

    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmPushSender> _logger;

    public FcmPushSender(string credentialsPath, ILogger<FcmPushSender> logger)
    {
        _logger = logger;
        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = CredentialFactory.FromFile<ServiceAccountCredential>(credentialsPath).ToGoogleCredential(),
        }, "walletmark");
        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        if (tokens.Count == 0) return [];

        var yanit = await _messaging.SendEachForMulticastAsync(new MulticastMessage
        {
            Tokens = tokens,
            Notification = new Notification { Title = message.Title, Body = message.Body },
            Data = message.Data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification { ChannelId = AndroidChannelId },
            },
        }, ct);

        var gecersiz = new List<string>();
        for (var i = 0; i < yanit.Responses.Count; i++)
        {
            var r = yanit.Responses[i];
            if (r.IsSuccess) continue;
            // Yalnizca kalici hatalarda token silinir; kota/gecici hatada tutulur.
            if (r.Exception?.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                gecersiz.Add(tokens[i]);
            else
                _logger.LogWarning("FCM gonderimi basarisiz: {Code}", r.Exception?.MessagingErrorCode);
        }

        _logger.LogInformation("FCM: {Ok}/{Total} cihaza gonderildi, {Invalid} gecersiz token",
            yanit.SuccessCount, tokens.Count, gecersiz.Count);
        return gecersiz;
    }
}
