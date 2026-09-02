namespace SmartFinance.Application.Common;

/// <summary>
/// Ücretsiz katmanın sınırları — TEK KAYNAK.
///
/// Sınırlar hem sunucuda uygulanıyor hem de uygulamaya bildiriliyor (paywall
/// ekranı ve "3/5 yatırım" göstergeleri için). İki tarafta ayrı ayrı yazılsaydı
/// biri değiştiğinde diğeri sessizce yanlış kalırdı.
///
/// Tasarım ilkesi: gelir-gider takibinin kendisi HİÇBİR ZAMAN sınırlanmaz.
/// İşlem veya kategori sayısını kısmak, bir finans uygulamasının temel işlevini
/// sakatlar ve kullanıcıyı ilk haftada kaybettirir. Sınırlar yalnızca "yatırım
/// takibi yapan ileri kullanıcı" tarafında.
/// </summary>
public static class FreeTierLimits
{
    /// Ücretsiz katmanda aynı anda tutulabilecek yatırım pozisyonu sayısı.
    public const int Investments = 5;

    /// Ücretsiz katmanda tanımlanabilecek bütçe sayısı.
    public const int Budgets = 3;

    /// Ücretsiz katmanda takvim ayı başına ekstre içe aktarma sayısı.
    public const int ImportsPerMonth = 2;

    /// Teknik göstergeler (RSI, MACD vb.) yalnızca premium'da.
    /// Fiyat grafiği ücretsiz katmanda da açık — kullanıcı neyi kaçırdığını
    /// görmeden ödeme yapmaz.
    public const bool IndicatorsIncluded = false;
}
