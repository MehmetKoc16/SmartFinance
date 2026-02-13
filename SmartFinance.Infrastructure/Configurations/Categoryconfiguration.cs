using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(c=>c.Id);

        builder.Property(c=>c.Name).IsRequired().HasMaxLength(100);

        builder.HasOne(c=>c.User).WithMany(u=>u.Categories).HasForeignKey(c=>c.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(c=>!c.IsDeleted);    
    }
}