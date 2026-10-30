using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFinance.Infrastructure.Migrations
{
    /// <summary>
    /// Bildirim turune gore kullanici tercihleri (KVKK 14.01.2026 duyurusu, 6563 sayili Kanun).
    ///
    /// ELLE DUZELTILDI: EF modelde varsayilan deger olmadigi icin (HasDefaultValue
    /// bilerek kullanilmiyor, bkz. User.cs) iskele uc bool kolon icin de
    /// defaultValue: false uretir. Bu, mevcut tum kullanicilarin butce uyarisi ve
    /// planli odeme push'unu sessizce kapatirdi. Asagidaki defaultValue'lar
    /// YALNIZCA mevcut satirlari doldurmak icin:
    ///   NotifyBudgetAlerts       = true  (onceki davranis: push acikti)
    ///   NotifyRecurringReminders = true  (onceki davranis: push acikti)
    ///   NotifyAnnouncements      = false (on onay yok; opt-in gerekir)
    /// SQL Server bunlari kolonda DEFAULT kisiti olarak birakir. Uygulama bu kisitlara
    /// guvenmez: modelde varsayilan olmadigi icin EF her INSERT'te uc degeri de acikca
    /// yazar (yeni kullanicinin degeri C# ozellik baslaticisindan gelir). Kisitin
    /// kalmasi, dagitim sirasinda/geri alista hala calisan eski surumun (bu
    /// kolonlari bilmeyen) kullanici kaydi INSERT'inin NOT NULL hatasina dusmemesini saglar.
    /// Tarih kolonlari bos baslar: mevcut kullanicilarin kayitli onayi yok.
    /// </summary>
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifyBudgetAlerts",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyRecurringReminders",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyAnnouncements",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncementsConsentAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncementsOptOutAt",
                table: "Users",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not: geri almak onay/geri cekme tarihlerini (6563 kaniti) siler.
            migrationBuilder.DropColumn(name: "AnnouncementsOptOutAt", table: "Users");
            migrationBuilder.DropColumn(name: "AnnouncementsConsentAt", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyAnnouncements", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyRecurringReminders", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyBudgetAlerts", table: "Users");
        }
    }
}
