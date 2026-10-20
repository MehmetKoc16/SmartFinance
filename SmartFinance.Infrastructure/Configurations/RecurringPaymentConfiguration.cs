using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class RecurringPaymentConfiguration : IEntityTypeConfiguration<RecurringPayment>
{
    public void Configure(EntityTypeBuilder<RecurringPayment> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Amount).IsRequired().HasColumnType("decimal(18,2)");
        // HasDefaultValue(1) KULLANILMAZ: EF int için 0'ı 'değer yok' sayıp INSERT'e koymuyor,
        // SQL varsayılanı 1 yazılıyordu ('Günü gelince' = 0 kayboluyordu). Başlangıç değeri entity'de (= 1).
        // Mevcut vade/aktiflik eşzamanlı pay ve skip isteklerinin ikisini birden kabul etmez.
        // RowVersion gibi mevcut tablolara veya yeni tabloya ek sütun gerekmez.
        builder.Property(x => x.NextDueDate).IsConcurrencyToken();
        builder.Property(x => x.IsActive).IsConcurrencyToken();
        builder.Property(x => x.IsDeleted).IsConcurrencyToken();
        builder.HasIndex(x => new { x.UserId, x.NextDueDate });
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
