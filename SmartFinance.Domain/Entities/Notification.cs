using SmartFinance.Domain.Common;
using SmartFinance.Domain.Enums;

namespace SmartFinance.Domain.Entities;

public class Notification : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }

    // Ayni donem icin tekrar tekrar bildirim uretilmesini onlemek icin
    // (orn. butce asimi: "{categoryId}:{year}-{month}"). Tekillik
    // UserId+DedupeKey uzerinden — diger bildirim tiplerinde bos kalabilir.
    public string? DedupeKey { get; set; }
}
