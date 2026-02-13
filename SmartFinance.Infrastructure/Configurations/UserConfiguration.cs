using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u=>u.Id);

        builder.Property(u=>u.FullName).IsRequired().HasMaxLength(100);
        
        builder.Property(u=>u.Email).IsRequired().HasMaxLength(150);
        builder.HasIndex(u=>u.Email).IsUnique();

        builder.Property(u=>u.PasswordHash).IsRequired();

        builder.HasQueryFilter(u=>!u.IsDeleted);
    }
}