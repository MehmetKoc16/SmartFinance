using Microsoft.EntityFrameworkCore;
using SmartFinance.Domain.Entities;

namespace SmartFinance.Infrastructure.Context;

public class SmartFinanceDbContext : DbContext
{
    public SmartFinanceDbContext(DbContextOptions<SmartFinanceDbContext> options) : base(options)
    {}
    public DbSet<User> Users {get;set;}
    public DbSet<Transaction> Transactions {get;set;}
    public DbSet<Category> Categories {get;set;}
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartFinanceDbContext).Assembly);

        // Test kullanıcısı (JWT eklenince kaldırılacak)
        modelBuilder.Entity<User>().HasData(new User
        {
            Id = 1,
            FullName = "Test Kullanıcı",
            Email = "test@smartfinance.com",
            PasswordHash = "test123",
            CreatedDate = new DateTime(2025, 1, 1)
        });
    }
}