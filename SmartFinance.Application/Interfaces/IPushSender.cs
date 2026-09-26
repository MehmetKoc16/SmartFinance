namespace SmartFinance.Application.Interfaces;

public record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Telefona anlik bildirim (push) gonderir. Uygulama ici bildirim her zaman
/// veritabanina yazilir; push yalnizca ek bir kanal, gonderilemezse is akisi
/// bozulmaz.
/// </summary>
public interface IPushSender
{
    /// Artik gecersiz olan (uygulama kaldirilmis, token yenilenmis) token'lari
    /// doner; cagiran bunlari siler.
    Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default);
}
