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
using SmartFinance.Application.Exceptions;

namespace SmartFinance.Infrastructure.Services;

public class AuthService : IAuthService{
    private readonly SmartFinanceDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUserService;

    public AuthService(SmartFinanceDbContext context, IConfiguration configuration, ICurrentUserService currentUserService)
    {
        _context=context;
        _configuration=configuration;
        _currentUserService = currentUserService;
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
