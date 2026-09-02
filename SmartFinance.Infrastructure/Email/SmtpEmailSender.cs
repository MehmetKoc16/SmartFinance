using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.Infrastructure.Email;

/// <summary>
/// SMTP üzerinden e-posta gönderir.
///
/// Neden SMTP (sağlayıcıya özel HTTP API değil): Gmail, Brevo, Resend,
/// Mailgun — hepsi SMTP sunuyor. Sağlayıcı değiştiğinde yalnızca
/// yapılandırma değişiyor, kod aynı kalıyor.
///
/// Yapılandırma yoksa e-posta GÖNDERİLMEZ, uyarı loglanır ve çağıran taraf
/// hata almaz. Gerekçe: şifre sıfırlama isteği, e-posta altyapısı çökse bile
/// kullanıcıya 500 döndürmemeli — ama sunucuda sessizce kaybolmamalı da.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var host = _configuration["Email:SmtpHost"];
        var user = _configuration["Email:SmtpUser"];
        var pass = _configuration["Email:SmtpPassword"];
        var from = _configuration["Email:FromAddress"];
        var fromName = _configuration["Email:FromName"] ?? "Wallet Mark";

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            // Kurulmadan once gelistirmede calisabilmek icin: gonderilemedi
            // ama konu ve alici loglaniyor ki akis test edilebilsin.
            _logger.LogWarning(
                "E-posta yapılandırması eksik, gönderim ATLANDI. Alıcı: {To} — Konu: {Subject}",
                Maskele(toEmail), subject);
            return;
        }

        var port = int.TryParse(_configuration["Email:SmtpPort"], out var p) ? p : 587;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        // 465 dogrudan TLS, 587 STARTTLS — yaygin iki kurulum.
        var secure = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        await client.ConnectAsync(host, port, secure, ct);
        if (!string.IsNullOrWhiteSpace(user))
            await client.AuthenticateAsync(user, pass, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("E-posta gönderildi. Alıcı: {To} — Konu: {Subject}", Maskele(toEmail), subject);
    }

    /// <summary>
    /// E-posta adresini loga yazılabilir hale getirir: a***@ornek.com
    ///
    /// Adres kişisel veri; sunucu logları KVKK kapsamında ve journald'ı
    /// okuyabilen herkese açık. Tam adres yerine maskesi yazılıyor —
    /// sorun ayıklamak için hangi hesap olduğu yine ayırt edilebiliyor.
    /// </summary>
    private static string Maskele(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var bas = email[0];
        return $"{bas}***{email[at..]}";
    }
}
