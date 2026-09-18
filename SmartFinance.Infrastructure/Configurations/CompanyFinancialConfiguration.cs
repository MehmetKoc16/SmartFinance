using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class CompanyFinancialConfiguration : IEntityTypeConfiguration<CompanyFinancial>
{
    public void Configure(EntityTypeBuilder<CompanyFinancial> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        builder.Property(x => x.DisclosureId).HasMaxLength(20);
        // THYAO ozkaynagi ~1,0e12 TL; 20 basamak bol yetiyor.
        builder.Property(x => x.NetProfitParent).HasColumnType("decimal(20,2)");
        builder.Property(x => x.EquityParent).HasColumnType("decimal(20,2)");

        builder.HasIndex(x => new { x.Symbol, x.Year, x.Period }).IsUnique();
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
