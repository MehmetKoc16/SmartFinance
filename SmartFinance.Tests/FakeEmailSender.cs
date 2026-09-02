using SmartFinance.Application.Interfaces;

namespace SmartFinance.Tests;

/// Testlerde gercek e-posta gondermek yerine gonderileni yakalar.
/// Sifre sifirlama akisinda baglantiyi govdeden cikarabilmek icin gerekli.
public class FakeEmailSender : IEmailSender
{
    public record Gonderilen(string To, string Subject, string HtmlBody);

    public List<Gonderilen> Kutu { get; } = new();

    /// Doluysa gonderim bunu firlatir. SMTP kesintisini (Brevo anahtarinin
    /// suresi dolmus, IP izni dusmus) testte canlandirmak icin.
    public Exception? Hata { get; set; }

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (Hata != null) throw Hata;
        Kutu.Add(new Gonderilen(toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    /// Son gonderilen e-postadaki sifirlama token'i.
    public string? SonToken()
    {
        if (Kutu.Count == 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(Kutu[^1].HtmlBody, @"token=([A-Za-z0-9_\-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
