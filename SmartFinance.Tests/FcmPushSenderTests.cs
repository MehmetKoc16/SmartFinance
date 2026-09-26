using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Push;

namespace SmartFinance.Tests;

public class FcmPushSenderTests
{
    private static readonly PushMessage Mesaj = new("Bütçe limiti aşıldı", "Market kategorisinde limitinizi aştınız.",
        new Dictionary<string, string> { ["type"] = "notification", ["notificationId"] = "7" });

    /// Regresyon (26.09.2026, cihazda): bildirim saati "3.01.1" gorunuyordu;
    /// SDK bos EventTimestamp'i 0001-01-01 olarak gonderiyor.
    [Fact]
    public void BildirimZamani_GonderimAni()
    {
        var simdi = new DateTime(2026, 9, 26, 9, 19, 0, DateTimeKind.Utc);

        var m = FcmPushSender.MesajOlustur(["tel"], Mesaj, simdi);

        Assert.Equal(simdi, m.Android.Notification.EventTimestamp);
    }

    [Fact]
    public void Mesaj_KanalBaslikVeVeriyiTasir()
    {
        var m = FcmPushSender.MesajOlustur(["tel-1", "tel-2"], Mesaj, DateTime.UtcNow);

        Assert.Equal(["tel-1", "tel-2"], m.Tokens);
        Assert.Equal("Bütçe limiti aşıldı", m.Notification.Title);
        Assert.Equal(FcmPushSender.AndroidChannelId, m.Android.Notification.ChannelId);
        Assert.Equal("notification", m.Data["type"]);
    }
}
