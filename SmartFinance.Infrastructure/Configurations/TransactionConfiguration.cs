using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t=>t.Id);

        builder.Property(t=>t.Amount).IsRequired().HasColumnType("decimal(18,2)");

        builder.Property(t=>t.Description).HasMaxLength(500);

        builder.Property(t=>t.TransactionDate).IsRequired();

        builder.HasOne(t=>t.User).WithMany(u=>u.Transactions).HasForeignKey(t=>t.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t=>t.Category).WithMany(c=>c.Transactions).HasForeignKey(t=>t.CategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(t=>!t.IsDeleted);
    }
}