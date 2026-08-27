using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class PriceHistoryConfiguration : IEntityTypeConfiguration<PriceHistory>
{
    public void Configure(EntityTypeBuilder<PriceHistory> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.InvestmentType)
            .IsRequired()
            .HasMaxLength(20);

        // TEFAS fiyatlari 6 ondalik basamakla yayinliyor (ornek: 1,215876).
        builder.Property(x => x.Close).HasColumnType("decimal(18,6)");
        builder.Property(x => x.Open).HasColumnType("decimal(18,6)");
        builder.Property(x => x.High).HasColumnType("decimal(18,6)");
        builder.Property(x => x.Low).HasColumnType("decimal(18,6)");
        // Hacim buyuk sayilara ulasabiliyor; saglayicilar bazen kesirli donuyor.
        builder.Property(x => x.Volume).HasColumnType("decimal(20,2)");

        // Ayni sembol+tip icin ayni gun iki kez yazilamaz — senkron isi tekrar
        // calistiginda mukerrer kayit olusmasini engeller.
        // Kolon sirasi grafik sorgusunu da karsiliyor: aramalar her zaman
        // "su sembol, su tip, su tarih araligi" seklinde geliyor.
        builder.HasIndex(x => new { x.Symbol, x.InvestmentType, x.Date }).IsUnique();

        // Kullaniciya ait bir kayit degil, paylasilan piyasa verisi —
        // diger varliklarin aksine soft-delete filtresi uygulanmiyor.
    }
}
