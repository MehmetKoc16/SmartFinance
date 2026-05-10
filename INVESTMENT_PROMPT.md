# SmartFinance Backend — Yatırım (Investment) Modülü

## Görev
Mevcut SmartFinance C# .NET 9 backend projesine **Investment (Yatırım)** modülünü ekle. Projedeki **Transaction** modülünün birebir aynı pattern'ini kullan.

## Proje Mimarisi (Katmanlı)
```
SmartFinance.Domain/          → Entity'ler, BaseEntity, Enum'lar
SmartFinance.Application/     → DTO'lar, Interface'ler (servis kontratları)
SmartFinance.Infrastructure/  → DbContext, Configuration, Service implementasyonları, Repository
SmartFinance.API/             → Controller'lar, Program.cs (DI), Middleware
```

## Mevcut Pattern (Transaction modülü — bunu örnek al)

### BaseEntity (tüm entity'ler bundan türer):
```csharp
namespace SmartFinance.Domain.Common;
public abstract class BaseEntity
{
    public int Id {get;set;}
    public DateTime CreatedDate {get;set;} = DateTime.UtcNow;
    public DateTime? UpdatedDate{get;set;}
    public bool IsDeleted {get;set;}=false;
}
```

### Transaction Entity:
```csharp
using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;
namespace SmartFinance.Domain.Entities;
public class Transaction : BaseEntity
{
    public decimal Amount {get;set;}
    public string Description {get;set;}=string.Empty;
    public DateTime TransactionDate {get;set;}
    public TransactionType Type{get;set;}
    public int UserId {get;set;}
    public int CategoryId {get;set;}
    public User User{get;set;}=null!;
    public Category Category{get;set;}=null!;
}
```

### DbContext:
```csharp
public class SmartFinanceDbContext : DbContext
{
    public SmartFinanceDbContext(DbContextOptions<SmartFinanceDbContext> options) : base(options) {}
    public DbSet<User> Users {get;set;}
    public DbSet<Transaction> Transactions {get;set;}
    public DbSet<Category> Categories {get;set;}
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SmartFinanceDbContext).Assembly);
    }
}
```

### ITransactionService (interface örneği):
```csharp
using SmartFinance.Application.DTOs.Transaction;
namespace SmartFinance.Application.Interfaces;
public interface ITransactionService{
    Task<object> GetFilteredTransactionsAsync(TransactionFilterDto filter);
    Task<IEnumerable<TransactionDto>> GetAllTransactionsAsync();
    Task<TransactionDto?> GetTransactionByIdAsync(int id);
    Task<TransactionDto> CreateTransactionAsync(CreateTransactionDto dto);
    Task UpdateTransactionAsync(int id, CreateTransactionDto dto);
    Task DeleteTransactionAsync(int id);
    Task<MonthlySummaryDto> GetMonthlySummaryAsync(int month, int year);
}
```

### TransactionController (controller örneği):
```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using SmartFinance.Application.DTOs.Transaction;
using SmartFinance.Application.Interfaces;
namespace SmartFinance.API.Controllers{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class TransactionController : ControllerBase
    {
        private readonly ITransactionService _transactionService;
        public TransactionController(ITransactionService transactionService)
        {
            _transactionService = transactionService;
        }
        // ... CRUD endpoint'leri
    }
}
```

### Program.cs (DI kayıtları):
```csharp
builder.Services.AddScoped(typeof(IGenericRepository<>),typeof(GenericRepository<>));
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IAuthService, AuthService>();
```

### Service'lerde UserId alma pattern'i:
```csharp
private readonly IGenericRepository<Transaction> _repository;
private readonly IHttpContextAccessor _httpContextAccessor;

private int GetUserId() =>
    int.Parse(_httpContextAccessor.HttpContext!.User.FindFirst("UserId")!.Value);
```

### GenericRepository interface:
```csharp
public interface IGenericRepository<T> where T : BaseEntity
{
    Task<IEnumerable<T>> GetAllAsync();
    Task<T?> GetByIdAsync(int id);
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(T entity);
    IQueryable<T> GetQueryable();
}
```

## Investment Entity Property'leri
```
Name          : string    → "THYAO", "Altın (gr)", "USD/TRY", "BTC"
FullName      : string    → "Türk Hava Yolları", "Gram Altın", "Bitcoin"
PurchasePrice : decimal   → Alış fiyatı
CurrentPrice  : decimal   → Güncel fiyat
Quantity      : double    → Adet veya gram
InvestmentType: string    → "stock", "gold", "currency", "crypto"
UserId        : int       → FK → User
```

## Yazılacak Dosyalar

### 1. `SmartFinance.Domain/Entities/Investment.cs`
Investment entity — BaseEntity'den türesin, UserId FK + User navigation

### 2. `SmartFinance.Application/DTOs/Investment/InvestmentDto.cs`
Çıkış DTO — Id dahil tüm property'ler + CreatedDate

### 3. `SmartFinance.Application/DTOs/Investment/CreateInvestmentDto.cs`
Giriş DTO — Id hariç, UserId hariç (token'dan alınacak)

### 4. `SmartFinance.Application/Interfaces/IInvestmentService.cs`
Interface — GetAll, GetById, Create, Update, Delete, GetPortfolioSummary

### 5. `SmartFinance.Infrastructure/Configurations/InvestmentConfiguration.cs`
EF Core config — IEntityTypeConfiguration<Investment>
(TransactionConfiguration ile aynı pattern: Property uzunlukları, ilişkiler, IsDeleted query filter)

### 6. `SmartFinance.Infrastructure/Services/InvestmentService.cs`
Servis — IInvestmentService implementasyonu
- GenericRepository + HttpContextAccessor kullan
- GetUserId() ile kullanıcının kendi verisini getir
- Soft delete (IsDeleted = true)
- GetPortfolioSummary: Toplam portföy değeri, toplam kar/zarar hesapla

### 7. `SmartFinance.API/Controllers/InvestmentController.cs`
Controller — [Authorize] ile korumalı, CRUD + summary endpoint'leri

### 8. Var olan dosya değişiklikleri:
- `SmartFinanceDbContext.cs` → `public DbSet<Investment> Investments {get;set;}` ekle
- `Program.cs` → `builder.Services.AddScoped<IInvestmentService, InvestmentService>();` ekle

## API Endpoint'ler (beklenen)
```
GET    /api/investment              → Kullanıcının tüm yatırımları
GET    /api/investment/{id}         → Tek yatırım
POST   /api/investment              → Yeni yatırım ekle
PUT    /api/investment/{id}         → Yatırım güncelle
DELETE /api/investment/{id}         → Yatırım sil (soft delete)
GET    /api/investment/summary      → Portföy özeti (toplam değer, kar/zarar)
```

## Önemli Kurallar
1. Transaction modülündeki pattern'i birebir takip et
2. Tüm servis metotlarında `GetUserId()` ile veri izolasyonu yap
3. Soft delete kullan (IsDeleted = true, veritabanından silme)
4. Configuration'da `HasQueryFilter(x => !x.IsDeleted)` ekle
5. Namespace'ler projedeki mevcut convention'a uygun olsun
6. Her dosyanın tam yolunu ve içeriğini ver

## Migration komutu (en son çalıştırılacak):
```bash
dotnet ef migrations add AddInvestment --project SmartFinance.Infrastructure --startup-project SmartFinance.API
dotnet ef database update --project SmartFinance.Infrastructure --startup-project SmartFinance.API
```
