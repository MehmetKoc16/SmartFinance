namespace SmartFinance.Application.Interfaces;

/// <summary>
/// Birden fazla sembolün güncel fiyatını TEK dış istekte alabilen sağlayıcılar
/// bu arayüzü uygular.
///
/// Neden gerekli: sembol başına ayrı istek atmak, dış servis hız sınırlarını
/// kullanıcı sayısıyla birlikte büyüyen bir yüke bağlar. Toplu istek + arka plan
/// yenileme ile dış istek sayısı yalnızca FARKLI SEMBOL sayısına bağlı hale
/// gelir; 1.000 kullanıcı da 100.000 kullanıcı da olsa aynı kalır.
///
/// Tüm sağlayıcılar desteklemek zorunda değil (örneğin TEFAS'ta toplu uç yok);
/// desteklemeyenler için tek tek istek yoluna düşülür.
/// </summary>
public interface IBatchPriceProvider
{
    /// Verilen sembollerin güncel fiyatlarını döner.
    /// Anahtar: girişte verilen sembolün kendisi (büyük harfe normalize edilmiş).
    /// Fiyatı alınamayan semboller sonuçta yer almaz — çağıran taraf eksikleri
    /// tek tek deneyebilir veya atlayabilir.
    Task<IReadOnlyDictionary<string, decimal>> GetCurrentPricesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default);

    /// Tek istekte gönderilebilecek azami sembol sayısı.
    int MaxBatchSize { get; }
}
