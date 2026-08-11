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
    public DbSet<Investment> Investments {get;set;}
    public DbSet<CategoryMapping> CategoryMappings {get;set;}
    public DbSet<RefreshToken> RefreshTokens {get;set;}
    public DbSet<Budget> Budgets {get;set;}
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartFinanceDbContext).Assembly);
    }
}