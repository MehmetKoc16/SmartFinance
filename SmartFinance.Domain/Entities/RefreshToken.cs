using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class RefreshToken : BaseEntity
{
    /// <summary>
    /// Refresh token'in DUZ degeri DEGIL, SHA-256 ozeti (AuthService.HashToken:
    /// Base64, 44 karakter). Duz deger yalnizca istemcide durur; DB/yedek
    /// sizarsa oturumlar dogrudan ele gecirilemesin diye.
    /// </summary>
    public string Token {get;set;}=string.Empty;
    public DateTime ExpiresAt {get;set;}
    public DateTime? RevokedAt {get;set;}
    public int UserId {get;set;}
    public User User {get;set;}=null!;

    public bool IsActive => RevokedAt == null && ExpiresAt > DateTime.UtcNow;
}
