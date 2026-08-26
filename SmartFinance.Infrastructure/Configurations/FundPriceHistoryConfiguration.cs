using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class FundPriceHistoryConfiguration : IEntityTypeConfiguration<FundPriceHistory>
{
    public void Configure(EntityTypeBuilder<FundPriceHistory> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FundCode)
            .IsRequired()
            .HasMaxLength(10);

        // TEFAS fiyatlari 6 ondalik basamakla yayinliyor (ornek: 1,215876).
        builder.Property(x => x.Price)
            .HasColumnType("decimal(18,6)");

        // Ayni fon icin ayni gun iki kez yazilamaz — senkron isi tekrar
        // calistiginda mukerrer kayit olusmasini engeller.
        // Ayni indeks grafik sorgusunu da karsiliyor: aramalar her zaman
        // "su fon, su tarih araligi" seklinde geliyor ve kolon sirasi buna uygun.
        builder.HasIndex(x => new { x.FundCode, x.Date }).IsUnique();

        // Bu tablo kullaniciya ait bir kayit degil, paylasilan piyasa verisi —
        // diger varliklarin aksine soft-delete filtresi uygulanmiyor.
    }
}
