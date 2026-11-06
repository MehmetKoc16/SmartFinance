namespace SmartFinance.Infrastructure.Billing;

/// <summary>
/// Sunucunun tanıdığı onay metni sürümleri. Current değiştirildiğinde önceki
/// sürümleri izin listesinde tutun; eski istemcilerin onayları da doğrulanabilmelidir.
/// </summary>
public static class PurchaseConsentTexts
{
    public const string Current = "2026-11-06";
    public static bool IsAllowed(string v) => v == Current;
}
