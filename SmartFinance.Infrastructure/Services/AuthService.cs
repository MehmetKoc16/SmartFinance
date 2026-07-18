using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
using Microsoft.AspNetCore.Http;

namespace SmartFinance.Infrastructure.Services;

public class AuthService : IAuthService{
    private readonly SmartFinanceDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthService(SmartFinanceDbContext context, IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        _context=context;
        _configuration=configuration;
        _httpContextAccessor=httpContextAccessor;
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

        return GenerateToken(user);
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

        return GenerateToken(user);
    }

    // Token üret (private = sadece bu sınıf içinden çağrılabilir)
    private TokenDto GenerateToken(User user)
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

        // 6. Token'ı metne çevir ve DTO olarak döndür
        return new TokenDto
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Expiration = expiration
        };
    }

    public async Task ChangePasswordAsync(ChangePasswordDto dto)
    {
        var userId = int.Parse(_httpContextAccessor.HttpContext!.User
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var user = await _context.Users.FindAsync(userId)
            ?? throw new NotFoundException("Kullanıcı bulunamadı!");
        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            throw new BadRequestException("Mevcut şifre hatalı!");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        user.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }
}
