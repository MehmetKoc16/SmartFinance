using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Application.DTOs.Notification;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Token).HasMaxLength(DeviceTokenLimits.MaxTokenLength).IsRequired();

        // Bir token tek bir kullaniciya ait olabilir; ayni cihaz iki hesaba
        // birden bildirim almasin (hesap degistirince eski hesabin bildirimi
        // yeni kullanicinin ekranina dusmesin).
        builder.HasIndex(x => x.Token).IsUnique();
        builder.HasIndex(x => x.UserId);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
