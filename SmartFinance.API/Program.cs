using Microsoft.EntityFrameworkCore;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;
using Scalar.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SmartFinance.API.Infrastructure;
using SmartFinance.API.Middleware;
using SmartFinance.Infrastructure.MarketData;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SmartFinanceDbContext>(options=>
options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped(typeof(IGenericRepository<>),typeof(GenericRepository<>));
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers();
// OpenAPI'ye JWT güvenlik şeması ekle (Scalar'da Authorize butonu çıksın)
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IInvestmentService, InvestmentService>();
builder.Services.AddScoped<IPdfImportService, PdfImportService>();

// Fiyat sağlayıcıları — her biri IPriceProvider altında kayıtlı, MarketDataService IEnumerable<IPriceProvider> ile hepsini alır
builder.Services.AddHttpClient<IPriceProvider, CoinGeckoPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, YahooFinancePriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, TefasPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, TcmbPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, GoldPriceProvider>();
builder.Services.AddScoped<IMarketDataService, MarketDataService>();

// JWT Authentication yapılandırması
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options=>{
    options.AddPolicy("AllowFrontend",policy=>{
        policy.WithOrigins("http://localhost:3000","http://localhost:5173").AllowAnyHeader().AllowAnyMethod();
    });
});

// Login/register brute-force ve spam denemelerine karsi: ayni IP'den dakikada
// en fazla 5 istek. Diger uc noktalar (yetkilendirme gerektiren, zaten JWT ile
// korunan) bu sinirlamaya dahil degil.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
var app = builder.Build();
app.UseMiddleware<ExceptionMiddleware>();

if(app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();