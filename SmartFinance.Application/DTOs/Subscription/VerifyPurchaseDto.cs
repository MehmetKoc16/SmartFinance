using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.Subscription;

public class VerifyPurchaseDto
{
    [Required]
    public string ProductId { get; set; } = string.Empty;

    [Required]
    [MaxLength(450)]
    public string PurchaseToken { get; set; } = string.Empty;
}
