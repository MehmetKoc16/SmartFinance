namespace SmartFinance.Application.Interfaces;

public interface IDeviceTokenService
{
    Task RegisterAsync(string token);
    Task UnregisterAsync(string token);
}
