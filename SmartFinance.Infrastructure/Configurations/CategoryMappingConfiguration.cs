using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class CategoryMappingConfiguration : IEntityTypeConfiguration<CategoryMapping>
{
    public void Configure(EntityTypeBuilder<CategoryMapping> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MerchantKeyword)
            .IsRequired()
            .HasMaxLength(200);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Category)
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        // Aynı kullanıcı için aynı keyword tekrar olmasın
        builder.HasIndex(x => new { x.UserId, x.MerchantKeyword }).IsUnique();

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
