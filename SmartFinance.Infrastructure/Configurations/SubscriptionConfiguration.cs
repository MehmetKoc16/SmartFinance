using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProductId).IsRequired().HasMaxLength(100);
        builder.Property(x => x.PurchaseToken).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.OrderId).HasMaxLength(100);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ayni satin alma jetonu iki kez islenmemeli: tekrar gonderilen bir
        // dogrulama isteginin ikinci bir abonelik satiri olusturmasini engeller.
        // Jeton cok uzun oldugu icin SQL Server'in 900 baytlik indeks sinirina
        // takilmamak adina ilk 450 karakter uzerinden tekillestiriliyor.
        builder.Property(x => x.PurchaseToken).HasMaxLength(450);
        builder.HasIndex(x => x.PurchaseToken).IsUnique();

        // Premium kontrolu her istekte calisiyor: "bu kullanicinin suresi
        // gecmemis aboneligi var mi" sorgusu indekse otursun.
        builder.HasIndex(x => new { x.UserId, x.ExpiresAt });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
