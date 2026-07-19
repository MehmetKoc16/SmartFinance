using SmartFinance.Domain.Common;

namespace SmartFinance.Domain.Entities;

public class RefreshToken : BaseEntity
{
    public string Token {get;set;}=string.Empty;
    public DateTime ExpiresAt {get;set;}
    public DateTime? RevokedAt {get;set;}
    public int UserId {get;set;}
    public User User {get;set;}=null!;

    public bool IsActive => RevokedAt == null && ExpiresAt > DateTime.UtcNow;
}
