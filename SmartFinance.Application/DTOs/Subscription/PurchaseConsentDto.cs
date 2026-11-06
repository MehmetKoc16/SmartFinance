using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Subscription;

public class PurchaseConsentDto
{
    [Required]
    [MaxLength(64)]
    public string ProductId { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string TextVersion { get; set; } = string.Empty;
}
