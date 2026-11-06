using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class PurchaseConsentConfiguration : IEntityTypeConfiguration<PurchaseConsent>
{
    public void Configure(EntityTypeBuilder<PurchaseConsent> builder)
    {
        builder.ToTable("PurchaseConsents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProductId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.TextVersion).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ConsentedAt).IsRequired();
        builder.HasIndex(x => x.UserId);
        builder.Property(x => x.PurchaseToken).HasMaxLength(450);
        builder.Property(x => x.OrderId).HasMaxLength(128);
        builder.HasIndex(x => x.PurchaseToken);
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
