using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(x => x.Id);
        // Aynı token'ı okuyan iki istekten yalnızca ilk iptal işlemi kazanır.
        builder.Property(x => x.RevokedAt).IsConcurrencyToken();

        // Kolon token'in SHA-256 ozetini tutar (Base64, 44 karakter); 200 yeterli.
        // Ad ve uzunluk bilerek degistirilmedi: sema degisikligi gerekmesin,
        // HashRefreshTokens migration'i yalnizca veriyi donusturur.
        builder.Property(x => x.Token)
            .IsRequired()
            .HasMaxLength(200);

        // Arama her zaman ozet uzerinden yapilir.
        builder.HasIndex(x => x.Token).IsUnique();

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
