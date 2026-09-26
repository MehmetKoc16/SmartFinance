namespace SmartFinance.Application.DTOs.Notification;

public static class DeviceTokenLimits
{
    // FCM token'lari bugun ~160-200 karakter; Google uzunlugu garanti etmiyor.
    public const int MaxTokenLength = 512;

    // Bir kullanicinin en fazla bu kadar cihazi tutulur; fazlasi (en uzun
    // suredir gorulmeyen) silinir. Sinirsiz buyumeyi ve her bildirimde
    // yuzlerce istegi onler.
    public const int MaxTokensPerUser = 10;
}
