using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(200);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Dogrulama her zaman ozet uzerinden arar.
        builder.HasIndex(x => x.TokenHash).IsUnique();

        // "Bu kullanicinin bekleyen token'lari" sorgusu icin.
        builder.HasIndex(x => new { x.UserId, x.ExpiresAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
