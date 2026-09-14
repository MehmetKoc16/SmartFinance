using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Title).HasMaxLength(200);
        builder.Property(x => x.Message).HasMaxLength(1000);
        builder.Property(x => x.DedupeKey).HasMaxLength(100);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // SQL Server'da NULL degerler tekillik kisitina takilmaz, bu yuzden
        // DedupeKey kullanmayan (Info gibi) bildirim tiplerinde sorun cikmaz.
        builder.HasIndex(x => new { x.UserId, x.DedupeKey }).IsUnique();

        builder.HasIndex(x => new { x.UserId, x.CreatedDate });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
