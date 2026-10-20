namespace SmartFinance.Application.Common;

/// <summary>
/// Para alanlarının ortak sınırı: decimal(18,2) sütununa sığan, en fazla iki ondalık basamaklı değer.
/// Planlı ödeme, transfer, açılış bakiyesi ve kart limiti aynı kuralı kullanır.
/// İşaret (pozitif/negatif) kuralı alanın kendi doğrulamasındadır.
/// </summary>
public static class MoneyRules
{
    public const decimal Max = 9999999999999999.99m;

    public static bool FitsColumn(decimal amount) =>
        amount >= -Max && amount <= Max && decimal.Round(amount, 2) == amount;
}
