namespace SmartFinance.Application.DTOs.Auth;

public class TokenDto{
    public string Token{get;set;}=string.Empty;
    public DateTime Expiration{get;set;}
}