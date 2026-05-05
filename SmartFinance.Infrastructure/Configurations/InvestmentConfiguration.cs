using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class InvestmentConfiguration : IEntityTypeConfiguration<Investment>
{
    public void Configure(EntityTypeBuilder<Investment> builder)
    {
        builder.HasKey(x=>x.Id);

        builder.Property(x=>x.Name).IsRequired().HasMaxLength(50);
        builder.Property(x=>x.FullName).IsRequired().HasMaxLength(200);
        builder.Property(x=>x.PurchasePrice).IsRequired().HasPrecision(18,4);
        builder.Property(x=>x.CurrentPrice).IsRequired().HasPrecision(18,4);
        builder.Property(x=>x.Quantity).IsRequired();
        builder.Property(x=>x.InvestmentType).IsRequired().HasMaxLength(50);
        builder.HasQueryFilter(x=>!x.IsDeleted);
        builder.HasOne(x=>x.User).WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}