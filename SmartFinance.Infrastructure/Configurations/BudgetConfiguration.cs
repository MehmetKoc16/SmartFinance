using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MonthlyLimit)
            .HasColumnType("decimal(18,2)");

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Category)
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Kullanıcı başına kategori başına tek limit
        builder.HasIndex(x => new { x.UserId, x.CategoryId }).IsUnique();

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
