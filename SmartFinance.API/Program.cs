using Microsoft.EntityFrameworkCore;
using SmartFinance.Infrastructure.Context;
using SmartFinance.Application.Interfaces;
using SmartFinance.Infrastructure.Repositories;
using SmartFinance.Infrastructure.Services;
using Scalar.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFinance.API.Cli;
using SmartFinance.API.Infrastructure;
using SmartFinance.API.Middleware;
using SmartFinance.Infrastructure.Email;
using SmartFinance.Infrastructure.MarketData;
using SmartFinance.Infrastructure.Fundamentals;
using SmartFinance.Infrastructure.Push;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<SmartFinanceDbContext>(options=>
options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped(typeof(IGenericRepository<>),typeof(GenericRepository<>));
builder.Services.AddHttpContextAccessor();
// Piyasa verisi onbellegi — dis servislere (Yahoo/TEFAS/TCMB/CoinGecko) yapilan
// tekrarli istekleri onler. Singleton olmali ki tum istekler ayni onbellegi paylassin.
builder.Services.AddMemoryCache();

// Sunucu izleme (uptime monitoring) ve dagitim sonrasi dogrulama icin.
// Veritabani baglantisini da sinar: surec ayakta ama DB'ye ulasamiyorsa
// "saglikli" gorunmemeli.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SmartFinanceDbContext>(name: "database");

builder.Services.AddControllers();

// [ApiController] gecersiz modelde varsayilan olarak RFC 7807 ProblemDetails
// doner: {"title":"One or more validation errors occurred.","errors":{...}}.
// Istemci ise tum hatalarda oldugu gibi "message" alanina bakiyor — bu yuzden
// DTO'lardaki Turkce ErrorMessage metinleri kullaniciya hic ulasmiyor, yerine
// genel "İşlem başarısız" gosteriliyordu. Yanit, ExceptionMiddleware'in
// uretttigi bicimle ayni hale getiriliyor.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = ValidationProblemResponseFactory.Create;
});

// OpenAPI'ye JWT güvenlik şeması ekle (Scalar'da Authorize butonu çıksın)
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IRecurringPaymentService, RecurringPaymentService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IInvestmentService, InvestmentService>();
builder.Services.AddScoped<IPdfImportService, PdfImportService>();
builder.Services.AddScoped<IBudgetService, BudgetService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDeviceTokenService, DeviceTokenService>();

// Anlik bildirim (FCM). Anahtar dosyasi yoksa (gelistirme, test) push
// gonderilmez; uygulama ici bildirimler yine calisir.
var fcmAnahtari = builder.Configuration["Firebase:CredentialsPath"];
if (!string.IsNullOrWhiteSpace(fcmAnahtari) && File.Exists(fcmAnahtari))
{
    builder.Services.AddSingleton<IPushSender>(sp =>
        new FcmPushSender(fcmAnahtari, sp.GetRequiredService<ILogger<FcmPushSender>>()));
}
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// Fiyat sağlayıcıları — her biri IPriceProvider altında kayıtlı, MarketDataService IEnumerable<IPriceProvider> ile hepsini alır
// CoinGecko artik dogrudan kullanilmiyor: Binance'te listelenmeyen coin'ler
// (ornegin TON) icin BinanceCryptoPriceProvider'in yedegi olarak duruyor.
// Bu yuzden IPriceProvider olarak degil, kendi tipiyle kayitli.
builder.Services.AddHttpClient<CoinGeckoPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, BinanceCryptoPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, YahooFinancePriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, TefasPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, TcmbPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, GoldPriceProvider>();
builder.Services.AddHttpClient<IPriceProvider, SilverPriceProvider>();
builder.Services.AddScoped<IMarketDataService, MarketDataService>();

// F/K ve PD/DD icin KAP'tan donemlik net kar ve ozkaynak. Senkron servis
// portfoydeki hisseler icin 6 saatte bir KAP'a gider; istekler veritabanindan okur.
builder.Services.AddScoped<IFundamentalsService, FundamentalsService>();
builder.Services.AddHttpClient<IKapClient, KapClient>();
builder.Services.AddHostedService<KapFundamentalsSyncService>();
builder.Services.AddScoped<SmartFinance.Infrastructure.Recurring.RecurringReminderRunner>();
builder.Services.AddHostedService<SmartFinance.Infrastructure.Recurring.RecurringReminderService>();

// Premium hakki ve ucretsiz katman sinirlari. Sinirlarin uygulandigi TEK yer
// sunucu: istemcinin "ben premium'um" demesine guvenilmez.
// E-posta gonderimi (sifre sifirlama). SMTP uzerinden: saglayici degistiginde
// yalnizca yapilandirma degisiyor, kod ayni kaliyor.
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

builder.Services.AddScoped<IEntitlementService, EntitlementService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddSingleton<IPlayBillingClient, SmartFinance.Infrastructure.Billing.GooglePlayBillingClient>();

// Gunluk fiyat gecmisi (fon + hisse) kendi veritabanimizda saklanir: TEFAS tek
// istekte 1 aylik veri veriyor ve IP basina dakikada ~6 istekle siniriyor,
// Yahoo'nun siniri ise hic belgelenmemis. Bu sinirlar IP basina oldugu icin tum
// kullanicilar arasinda paylasiliyor; istek aninda cekmek olceklenmiyordu.
builder.Services.AddScoped<IPriceHistoryStore, PriceHistoryStore>();
// Dis kaynaga giden tek yer: gecelik senkron isi. Kullanici istekleri depodan okur.
builder.Services.AddHostedService<PriceHistorySyncService>();

// Guncel fiyat onbellegi. MarketDataService okur, PriceRefreshService yazar —
// ikisi de ayni anahtar bicimini kullansin diye tek sinif uzerinden gidiyor.
builder.Services.AddScoped<IPriceCache, PriceCache>();
// Hisse/kripto fiyatlarini arka planda TOPLU isteklerle yeniler. Boylece dis
// servise giden istek sayisi kullanici sayisindan bagimsiz hale gelir:
// yalnizca farkli sembol sayisina bagli kalir.
builder.Services.AddHostedService<PriceRefreshService>();
builder.Services.AddHostedService<SmartFinance.Infrastructure.Billing.SubscriptionRenewalService>();

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

// CORS yalnizca TARAYICI kaynakli istekleri ilgilendirir; mobil uygulama
// (Flutter) bu kontrole hic takilmaz. Buradaki liste ileride bir web paneli
// veya tanitim sitesi eklenirse gerekli olacak — bu yuzden dar tutuluyor.
builder.Services.AddCors(options=>{
    options.AddPolicy("AllowFrontend",policy=>{
        policy.WithOrigins(
                "https://walletmark.com.tr",
                "https://www.walletmark.com.tr",
                "http://localhost:3000",   // yerel gelistirme
                "http://localhost:5173")   // yerel gelistirme (Vite)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(ProxyHeaders.Configure);
builder.Services.AddRateLimiter(ApiRateLimiting.Configure);
var app = builder.Build();

// Tek seferlik yonetim komutlari (web sunucusu baslatilmaz). Bkz. Cli/DuyuruKomutu.
if (DuyuruKomutu.KomutMu(args))
{
    Environment.ExitCode = await DuyuruKomutu.CalistirAsync(args, app.Services);
    return;
}

app.UseForwardedHeaders();
app.UseMiddleware<ExceptionMiddleware>();

if(app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
// UseAuthentication'dan SONRA gelmeli: rate limit bolumlemesi User.Claims'teki
// kullanici kimligine bakiyor, kimlik dogrulama calismadan bu alan bos olur ve
// tum kullanicilar ayni kotayi paylasirdi.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

// Izleme araclari token tasimaz, bu yuzden anonim. Duzenli araliklarla
// cagrildigi icin rate limit disinda birakilir; aksi halde izleme trafigi
// sunucunun kendi kotasini tuketebilirdi.
app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();

app.Run();
