using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Notification;

public class DeviceTokenDto
{
    [Required(ErrorMessage = "Cihaz token'ı boş olamaz!")]
    [MaxLength(DeviceTokenLimits.MaxTokenLength, ErrorMessage = "Cihaz token'ı çok uzun!")]
    public string Token { get; set; } = string.Empty;
}
