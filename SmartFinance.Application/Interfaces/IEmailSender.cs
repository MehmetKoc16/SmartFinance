namespace SmartFinance.Application.Interfaces;

/// <summary>
/// E-posta gonderimi. Saglayicidan bagimsiz tutuluyor: bugun hangi servisi
/// kullandigimiz degisebilir, cagiran kodun bundan haberi olmamali.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}
