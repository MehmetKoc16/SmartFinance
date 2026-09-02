using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class ImportLogConfiguration : IEntityTypeConfiguration<ImportLog>
{
    public void Configure(EntityTypeBuilder<ImportLog> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // "Bu kullanici bu ay kac kez ice aktardi" sorgusu icin.
        builder.HasIndex(x => new { x.UserId, x.CreatedDate });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
