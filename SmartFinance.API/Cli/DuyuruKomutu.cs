using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Cli;

/// <summary>
/// Tum kullanicilara duyuru gonderen komut. Web sunucusu baslatilmaz; is bitince
/// program kapanir. HTTP ucu bilerek yok: internetten tetiklenemesin.
///
/// Sunucuda (ortam degiskenleri yuklu, root):
///   . /usr/local/bin/sf-env.sh
///   cd /var/www/smartfinance
///   dotnet SmartFinance.API.dll duyuru 2026-09-surum11 "Baslik" "Metin" [--yalnizca e@posta.com]
///
/// Ayni anahtarla tekrar calistirmak guvenli: daha once alan kimseye ikinci
/// kez gitmez. Once --yalnizca ile tek hesapta denenmesi onerilir.
/// </summary>
public static class DuyuruKomutu
{
    public sealed record Istek(string Anahtar, string Baslik, string Metin, string? YalnizcaEposta);

    public const string Kullanim =
        "Kullanim: dotnet SmartFinance.API.dll duyuru <anahtar> \"<baslik>\" \"<metin>\" [--yalnizca <eposta>]";

    /// Komut "duyuru" degilse null (normal web sunucusu baslar).
    public static bool KomutMu(string[] args) => args.Length > 0 && args[0] == "duyuru";

    /// Gecersiz argumanlarda ArgumentException.
    public static Istek Ayristir(string[] args)
    {
        var konumsal = new List<string>();
        string? eposta = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--yalnizca")
            {
                if (i + 1 >= args.Length) throw new ArgumentException("--yalnizca icin e-posta verilmedi.\n" + Kullanim);
                eposta = args[++i];
            }
            else
            {
                konumsal.Add(args[i]);
            }
        }
        if (konumsal.Count != 3) throw new ArgumentException(Kullanim);
        return new Istek(konumsal[0], konumsal[1], konumsal[2], eposta);
    }

    public static async Task<int> CalistirAsync(string[] args, IServiceProvider services)
    {
        try
        {
            var istek = Ayristir(args);
            using var scope = services.CreateScope();
            var bildirimler = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var sonuc = await bildirimler.BroadcastAsync(istek.Anahtar, istek.Baslik, istek.Metin, istek.YalnizcaEposta);
            Console.WriteLine(
                $"Duyuru '{istek.Anahtar}': {sonuc.NotifiedUsers} kullaniciya bildirim " +
                $"({sonuc.AlreadyNotifiedUsers} zaten almisti), {sonuc.PushTargets} cihaza push, " +
                $"{sonuc.InvalidTokensRemoved} gecersiz cihaz silindi.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
