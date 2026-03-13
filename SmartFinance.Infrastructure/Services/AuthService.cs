using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SmartFinance.Application.DTOs.Auth;
using SmartFinance.Application.Interfaces;
using SmartFinance.Domain.Entities;
using SmartFinance.Infrastructure.Context;
using Microsoft.EntityFrameworkCore;

namespace SmartFinance.Infrastructure.Services;

public class AuthService : IAuthService{
    private readonly SmartFinanceDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthService(SmartFinanceDbContext context, IConfiguration configuration)
    {
        _context=context;
        _configuration=configuration;
    }
    
    public async Task<TokenDto> RegisterAsync(RegisterDto dto)
    {
        var existingUser = await _context.Users.AnyAsync(u=> u.Email == dto.Email);
        if(existingUser)
        {
            throw new Exception("Bu email zaten kayıtlı!");
        }

        var user = new User{
            FullName= dto.FullName,
            Email= dto.Email,
            PasswordHash= BCrypt.Net.BCrypt.HashPassword(dto.Password)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return GenerateToken(user);
    }

    public async Task<TokenDto> LoginAsync(LoginDto dto)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u=>u.Email == dto.Email);
        if(user== null)
        {
            throw new Exception("Email veya şifre hatalı!");
        }

        var isPasswordValid = BCrypt.Net.BCrypt.Verify(dto.Password,user.PasswordHash);
        if(!isPasswordValid)
        {
            throw new Exception("Email veya şifre hatalı!");
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
}
