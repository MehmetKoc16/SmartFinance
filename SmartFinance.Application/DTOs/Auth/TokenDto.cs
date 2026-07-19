namespace SmartFinance.Application.DTOs.Auth;

public class TokenDto{
    public string Token{get;set;}=string.Empty;
    public DateTime Expiration{get;set;}
    // Erisim token'i (Token) suresi dolunca kullaniciyi tekrar login'e dusurmeden
    // yeni bir erisim token'i almak icin kullanilir. Cok daha uzun omurlu.
    public string RefreshToken{get;set;}=string.Empty;
}