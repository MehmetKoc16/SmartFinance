using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Recurring;

namespace SmartFinance.Tests;

public class RecurrenceScheduleTests
{
    // NEDEN: Kısa ay hedef günü kalıcı olarak kaydırmamalı.
    [Fact]
    public void Aylik_31Ocak_SubatSonGunu_SonraMart31()
    {
        var subat = RecurrenceSchedule.Next(new DateOnly(2027, 1, 31), RecurrenceFrequency.Monthly, 31);
        Assert.Equal(new DateOnly(2027, 2, 28), subat);
        Assert.Equal(new DateOnly(2027, 3, 31), RecurrenceSchedule.Next(subat, RecurrenceFrequency.Monthly, 31));
        Assert.Equal(new DateOnly(2028, 2, 29), RecurrenceSchedule.Next(new DateOnly(2028, 1, 31), RecurrenceFrequency.Monthly, 31));
    }

    // NEDEN: Artık yılın günü sonraki artık yılda geri gelmeli.
    [Fact]
    public void Yillik_29Subat_ArtikOlmayanYil28_ArtikYil29()
    {
        var tarih = RecurrenceSchedule.Next(new DateOnly(2024, 2, 29), RecurrenceFrequency.Yearly, 29);
        Assert.Equal(new DateOnly(2025, 2, 28), tarih);
        for (var i = 0; i < 3; i++) tarih = RecurrenceSchedule.Next(tarih, RecurrenceFrequency.Yearly, 29);
        Assert.Equal(new DateOnly(2028, 2, 29), tarih);
    }

    // NEDEN: Haftalık tekrar ay geçişinden etkilenmemeli.
    [Fact]
    public void Haftalik_YediGunEkler() => Assert.Equal(new DateOnly(2026, 11, 5),
        RecurrenceSchedule.Next(new DateOnly(2026, 10, 29), RecurrenceFrequency.Weekly, 29));

    // NEDEN: Pencere ve bitiş günleri dahil, bitiş sonrası hariç olmalı.
    [Fact]
    public void Pencere_EndDateSonrasiVadeUretmez()
    {
        var ilk = new DateOnly(2026, 10, 1);
        Assert.Equal(new[] { ilk, ilk.AddDays(7) }, RecurrenceSchedule.Occurrences(ilk,
            RecurrenceFrequency.Weekly, 1, ilk, ilk.AddDays(30), ilk.AddDays(7)));
    }

    // NEDEN: UTC 21:00 sonrası TSİ ertesi takvim günüdür.
    [Fact]
    public void TodayTr_UtcGeceYarisiSonrasi_TsiErtesiGun() => Assert.Equal(new DateOnly(2026, 10, 20),
        RecurrenceSchedule.TodayTr(new DateTime(2026, 10, 19, 21, 1, 0, DateTimeKind.Utc)));
}
