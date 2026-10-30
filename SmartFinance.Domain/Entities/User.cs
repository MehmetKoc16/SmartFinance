using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class User : BaseEntity
{
    public string FullName {get;set;}=string.Empty;
    public string Email {get;set;}=string.Empty;
    public string PasswordHash{get;set;}=string.Empty;

    // Bildirim turune gore push tercihleri (KVKK 14.01.2026 duyurusu: operasyonel
    // bildirimler ile kampanya/duyuru bildirimleri tek onayda birlestirilemez).
    // Varsayilanlar BILEREK ozellik baslaticisinda; EF yapilandirmasinda
    // HasDefaultValue YOK. HasDefaultValue(true) olsaydi EF, CLR varsayilani olan
    // false'u "deger verilmedi" sayip INSERT'ten cikarir ve veritabani true yazardi.
    public bool NotifyBudgetAlerts {get;set;} = true;
    public bool NotifyRecurringReminders {get;set;} = true;
    // Tanitim amacli duyurular 6563 sayili Kanun kapsaminda ticari elektronik
    // ileti sayilabilir: on onay (opt-in) gerekir, varsayilan kapali.
    public bool NotifyAnnouncements {get;set;} = false;
    // Onayin ve geri cekmenin tarihi (UTC). Yalnizca tercih gercekten degistiginde yazilir;
    // yeniden onay verilirse ConsentAt guncellenir, OptOutAt eski degeriyle kalir.
    public DateTime? AnnouncementsConsentAt {get;set;}
    public DateTime? AnnouncementsOptOutAt {get;set;}

    public ICollection<Transaction> Transactions {get;set;}= new List<Transaction>();
    public ICollection<Category> Categories {get;set;}=new List<Category>();
}