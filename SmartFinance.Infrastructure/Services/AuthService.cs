using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Domain.Enums;
using SmartFinance.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class AuthService : IAuthService{
    private readonly SmartFinanceDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUserService;

    // Sifirlama baglantisinin gecerlilik suresi. Kisa tutuluyor: e-posta
    // kutusuna sonradan erisen birinin eski baglantiyi kullanabilmesi riski.
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromMinutes(60);

    private readonly IEmailSender _emailSender;
    private readonly ILogger<AuthService> _logger;

    public AuthService(SmartFinanceDbContext context, IConfiguration configuration,
        ICurrentUserService currentUserService, IEmailSender emailSender, ILogger<AuthService> logger)
    {
        _context=context;
        _configuration=configuration;
        _currentUserService = currentUserService;
        _emailSender = emailSender;
        _logger = logger;
    }
    
    public async Task<TokenDto> RegisterAsync(RegisterDto dto)
    {
        var existingUser = await _context.Users.AnyAsync(u=> u.Email == dto.Email);
        if(existingUser)
        {
            throw new BadRequestException("Bu email zaten kayıtlı!");
        }

        var user = new User{
            FullName= dto.FullName,
            Email= dto.Email,
            PasswordHash= BCrypt.Net.BCrypt.HashPassword(dto.Password)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Yeni kullanıcıya varsayılan kategoriler oluştur (isim, tip, ikon, renk)
        var defaultCategories = new (string Name, TransactionType Type, string Icon, string Color)[]
        {
            ("Maaş", TransactionType.Income, "banknote", "#159A5B"),
            ("Yeme-İçme", TransactionType.Expense, "utensils", "#F43F5E"),
            ("Ulaşım", TransactionType.Expense, "car", "#14B8A6"),
            ("Fatura", TransactionType.Expense, "receipt", "#06B6D4"),
            ("ATM", TransactionType.Expense, "landmark", "#64748B"),
            ("Transfer", TransactionType.Expense, "arrow-left-right", "#3B82F6"),
            ("Alışveriş", TransactionType.Expense, "shopping-bag", "#8B5CF6"),
            ("Diğer", TransactionType.Expense, "more-horizontal", "#64748B"),
        };
        foreach (var cat in defaultCategories)
        {
            _context.Categories.Add(new Category
            {
                Name = cat.Name,
                Type = cat.Type,
                Icon = cat.Icon,
                Color = cat.Color,
                UserId = user.Id,
            });
        }
        await _context.SaveChangesAsync();

        return await GenerateTokenAsync(user);
    }

    public async Task<TokenDto> LoginAsync(LoginDto dto)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u=>u.Email == dto.Email);
        if(user== null)
        {
            throw new UnauthorizedException("Email veya şifre hatalı!");
        }

        var isPasswordValid = BCrypt.Net.BCrypt.Verify(dto.Password,user.PasswordHash);
        if(!isPasswordValid)
        {
            throw new UnauthorizedException("Email veya şifre hatalı!");
        }

        return await GenerateTokenAsync(user);
    }

    public async Task<TokenDto> RefreshTokenAsync(string refreshToken)
    {
        // Refresh token'in kendisi de bir "sifre" gibi davranilir — DB'de
        // birebir eslesen, henuz iptal edilmemis (RevokedAt=null) ve suresi
        // gecmemis (ExpiresAt>now) bir kayit aranir. IsActive bu ikisini kontrol eder.
        var existing = await _context.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (existing == null || !existing.IsActive)
            throw new UnauthorizedException("Oturum süresi dolmuş, lütfen tekrar giriş yapın.");

        // Rotasyon: kullanilan refresh token hemen iptal edilir, yerine yenisi
        // verilir. Boylece calinmis/sizmis bir token bir kez kullanildiktan
        // sonra tekrar kullanilamaz.
        existing.RevokedAt = DateTime.UtcNow;

        return await GenerateTokenAsync(existing.User);
    }

    public async Task LogoutAsync(string refreshToken)
    {
        var existing = await _context.RefreshTokens.FirstOrDefaultAsync(rt => rt.Token == refreshToken);
        if (existing == null) return; // zaten yok/iptal — sessizce cik
        existing.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    // Token üret (private = sadece bu sınıf içinden çağrılabilir).
    // Hem erisim (JWT) hem de yenileme (refresh) token'i uretip refresh
    // token'i DB'ye kaydeder — bu yuzden artik async.
    private async Task<TokenDto> GenerateTokenAsync(User user)
    {
        // 1. Token'a gömülecek bilgiler (Claims = iddialar/bilgiler)
        //    Bu bilgiler token içinde şifreli olarak taşınır
        var claims = new[]
        {
            // Sub = Subject (sâbcekt) = Kim bu token'ın sahibi?
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            // Email bilgisi
            new Claim(ClaimTypes.Email, user.Email),
            // Ad soyad bilgisi
            new Claim(ClaimTypes.Name, user.FullName)
        };

        // 2. Şifreleme anahtarı — appsettings'ten oku
        //    Key = anahtar, Encoding = kodlama
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));

        // 3. İmzalama bilgisi — hangi algoritma ile şifreliyoruz?
        //    HmacSha256 = güvenli şifreleme algoritması
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // 4. Token'ın geçerlilik süresi
        var expireMinutes = int.Parse(_configuration["Jwt:ExpireMinutes"]!);
        var expiration = DateTime.UtcNow.AddMinutes(expireMinutes);

        // 5. Token'ı oluştur
        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],      // Kim üretti?
            audience: _configuration["Jwt:Audience"],   // Kim kullanacak?
            claims: claims,                             // İçindeki bilgiler
            expires: expiration,                        // Ne zaman biter?
            signingCredentials: credentials             // İmza
        );

        // 6. Refresh token uret — 64 byte kriptografik olarak guvenli rastgele
        //    deger, Base64'e cevrilir. JWT'nin aksine icinde bilgi tasimaz,
        //    sadece DB'deki kaydiyla eslesen opak bir anahtar.
        var refreshTokenValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshTokenExpireDays = int.Parse(_configuration["Jwt:RefreshTokenExpireDays"] ?? "30");

        _context.RefreshTokens.Add(new RefreshToken
        {
            Token = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshTokenExpireDays),
            UserId = user.Id,
        });
        await _context.SaveChangesAsync();

        // 7. Token'ı metne çevir ve DTO olarak döndür
        return new TokenDto
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Expiration = expiration,
            RefreshToken = refreshTokenValue,
        };
    }

    public async Task ChangePasswordAsync(ChangePasswordDto dto)
    {
        var userId = _currentUserService.UserId;
        var user = await _context.Users.FindAsync(userId)
            ?? throw new NotFoundException("Kullanıcı bulunamadı!");
        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            throw new BadRequestException("Mevcut şifre hatalı!");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        user.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Şifre sıfırlama bağlantısı gönderir.
    ///
    /// E-posta kayıtlı OLMASA BİLE aynı yanıt dönüyor ve hata fırlatılmıyor.
    /// Aksi halde bu uç bir "hesap var mı" sorgulama aracına dönüşürdü:
    /// saldırgan e-posta listesini tek tek deneyip hangilerinin kayıtlı
    /// olduğunu öğrenebilirdi.
    /// </summary>
    public async Task ForgotPasswordAsync(ForgotPasswordDto dto, CancellationToken ct = default)
    {
        var email = dto.Email.Trim();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user == null)
        {
            _logger.LogInformation(
                "Şifre sıfırlama isteği kayıtlı olmayan bir adres için geldi; sessizce yok sayıldı.");
            return;
        }

        // Onceki bekleyen baglantilari gecersiz kil: kullanici arka arkaya
        // istek atarsa yalnizca sonuncusu calissin.
        var bekleyenler = await _context.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct);
        foreach (var eski in bekleyenler) eski.UsedAt = DateTime.UtcNow;

        // 32 baytlik kriptografik rastgele deger. URL'de tasinacagi icin
        // base64url (+ ve / yerine - ve _, dolgu yok).
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        _context.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(token),
            ExpiresAt = DateTime.UtcNow.Add(ResetTokenLifetime),
        });
        await _context.SaveChangesAsync(ct);

        var link = $"{_configuration["App:WebBaseUrl"] ?? "https://walletmark.com.tr"}/sifre-sifirla?token={token}";
        await _emailSender.SendAsync(user.Email, "Wallet Mark — Şifre Sıfırlama",
            BuildResetEmail(user.FullName, link), ct);

        _logger.LogInformation("Şifre sıfırlama bağlantısı gönderildi. UserId: {UserId}", user.Id);
    }

    /// <summary>
    /// Sıfırlama bağlantısındaki token ile yeni şifreyi belirler.
    /// </summary>
    public async Task ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default)
    {
        var hash = HashToken(dto.Token.Trim());

        var kayit = await _context.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Gecersiz, kullanilmis ve suresi dolmus icin AYNI mesaj: hangisinin
        // oldugunu soylemek saldirgana bilgi verir.
        if (kayit == null || kayit.UsedAt != null || kayit.ExpiresAt <= DateTime.UtcNow)
            throw new BadRequestException(
                "Bu sıfırlama bağlantısı geçersiz veya süresi dolmuş. Lütfen yeniden talep edin.");

        kayit.UsedAt = DateTime.UtcNow;
        kayit.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        kayit.User.UpdatedDate = DateTime.UtcNow;

        // Sifre degistiyse mevcut oturumlar da dusmeli: hesabi ele geciren
        // biri varsa yenileme token'iyla erisimini surdurememeli.
        var oturumlar = await _context.RefreshTokens
            .Where(r => r.UserId == kayit.UserId && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var oturum in oturumlar) oturum.RevokedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Şifre sıfırlandı. UserId: {UserId}", kayit.UserId);
    }

    private static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string BuildResetEmail(string fullName, string link) => $@"
<div style=""font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;color:#0f172a"">
  <h2 style=""margin:0 0 16px"">Şifre Sıfırlama</h2>
  <p>Merhaba {System.Net.WebUtility.HtmlEncode(fullName)},</p>
  <p>Wallet Mark hesabınız için şifre sıfırlama talebinde bulundunuz.
     Aşağıdaki bağlantıya tıklayarak yeni şifrenizi belirleyebilirsiniz.</p>
  <p style=""margin:24px 0"">
    <a href=""{link}"" style=""background:#3b82f6;color:#fff;padding:12px 22px;border-radius:10px;text-decoration:none;display:inline-block;font-weight:600"">
      Yeni şifre belirle
    </a>
  </p>
  <p style=""color:#475569;font-size:14px"">
    Bu bağlantı <strong>60 dakika</strong> geçerlidir ve yalnızca bir kez kullanılabilir.
  </p>
  <p style=""color:#475569;font-size:14px"">
    Bu talebi siz yapmadıysanız bu e-postayı yok sayabilirsiniz; şifreniz değişmez.
  </p>
  <hr style=""border:0;border-top:1px solid #e2e8f0;margin:24px 0"">
  <p style=""color:#94a3b8;font-size:12px"">
    Bağlantı çalışmıyorsa bu adresi tarayıcınıza yapıştırın:<br>{link}
  </p>
</div>";

    /// <summary>
    /// Hesabı ve kullanıcıya ait TÜM veriyi kalıcı olarak siler.
    ///
    /// Google Play, hesap oluşturmaya izin veren uygulamalarda hem uygulama
    /// içinden hem web üzerinden hesap silme yolu zorunlu tutuyor. KVKK'nın
    /// "silme" hakkı da gerçek silme istiyor — bu yüzden diğer işlemlerdeki
    /// gibi IsDeleted işaretlemek yeterli değil, satırlar tablodan kaldırılıyor.
    /// </summary>
    /// <summary>
    /// Oturumdaki kullaniciyi doner.
    ///
    /// Neden veritabanina bakiyor: JWT durumsuz ve 60 dakika gecerli. Kullanici
    /// hesabini sildikten sonra token teknik olarak hala imzali kaliyordu ve bu
    /// uc sadece token icindeki adi/e-postayi geri yansittigi icin uygulama
    /// "giris yapilmis" gorunmeye devam ediyordu. Kullanici artik yoksa 401
    /// donuluyor; istemcideki mevcut oturum-sonlandi akisi devreye giriyor.
    /// </summary>
    public async Task<object> GetMeAsync()
    {
        var userId = _currentUserService.UserId;
        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { id = u.Id, email = u.Email, fullName = u.FullName })
            .FirstOrDefaultAsync()
            ?? throw new UnauthorizedException("Oturum bilgisi geçersiz, lütfen tekrar giriş yapın.");

        return user;
    }

    public async Task DeleteAccountAsync(DeleteAccountDto dto)
    {
        var userId = _currentUserService.UserId;
        var user = await _context.Users.FindAsync(userId)
            ?? throw new NotFoundException("Kullanıcı bulunamadı!");

        // Silme geri alinamaz; telefonu acik unutulmus bir kullanicinin
        // hesabinin baskasinca silinmesini engellemek icin sifre yeniden isteniyor.
        if (!BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            throw new BadRequestException("Şifre hatalı!");

        // Tek islem: yarida kalirsa hicbiri uygulanmasin. Aksi halde kullanici
        // verisinin bir kismi silinip bir kismi kalabilirdi.
        await using var transaction = await _context.Database.BeginTransactionAsync();

        // IgnoreQueryFilters ZORUNLU: varliklarda "!IsDeleted" global filtresi var.
        // Filtre acikken daha once soft-delete edilmis satirlar gorunmez, ama
        // veritabaninda durmaya ve kategorilere yabanci anahtarla baglanmaya
        // devam ederler — Categories silinirken FK hatasi verirlerdi.
        //
        // Silme sirasi yabanci anahtar kisitlarina gore belirlendi:
        // Transactions hem User'a hem Category'ye Restrict ile bagli, bu yuzden
        // ikisinden de once gitmeli. Budgets ve CategoryMappings de Category'ye
        // bagli oldugu icin Categories'ten once siliniyor.
        _context.Transactions.RemoveRange(
            await _context.Transactions.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        _context.Budgets.RemoveRange(
            await _context.Budgets.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        _context.CategoryMappings.RemoveRange(
            await _context.CategoryMappings.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        _context.Investments.RemoveRange(
            await _context.Investments.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        _context.RefreshTokens.RemoveRange(
            await _context.RefreshTokens.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        await _context.SaveChangesAsync();

        _context.Categories.RemoveRange(
            await _context.Categories.IgnoreQueryFilters().Where(x => x.UserId == userId).ToListAsync());
        await _context.SaveChangesAsync();

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        await transaction.CommitAsync();

        // Not: PriceHistories kasten dokunulmuyor. O tablo kullaniciya degil
        // piyasaya ait paylasilan veri; silinmesi diger kullanicilarin
        // grafiklerini bozardi ve kisisel veri icermiyor.
    }

    public async Task<object> UpdateProfileAsync(UpdateProfileDto dto)
    {
        var userId = _currentUserService.UserId;
        var user = await _context.Users.FindAsync(userId)
            ?? throw new NotFoundException("Kullanıcı bulunamadı!");

        // Email degistiriliyorsa baska bir kullanici zaten bu email'i almis mi kontrol et
        if (!string.Equals(user.Email, dto.Email, StringComparison.OrdinalIgnoreCase))
        {
            var emailTaken = await _context.Users.AnyAsync(u => u.Id != userId && u.Email == dto.Email);
            if (emailTaken)
                throw new BadRequestException("Bu email zaten kullanılıyor!");
        }

        user.FullName = dto.FullName;
        user.Email = dto.Email;
        user.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Not: mevcut JWT'nin icindeki eski ad/email claim'leri bu istek
        // tamamlandiktan sonra da bir sonraki token yenilemesine kadar aynen
        // kalir (JWT icerigi imzalandiktan sonra degistirilemez) - bu yuzden
        // guncel degerleri buradan dogrudan donuyoruz, GetMe()'nin token'dan
        // okumasini beklemiyoruz.
        return new { id = user.Id, email = user.Email, fullName = user.FullName };
    }
}
