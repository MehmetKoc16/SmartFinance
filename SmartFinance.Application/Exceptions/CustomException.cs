namespace SmartFinance.Application.Exceptions;

public class BadRequestException : Exception
{
    public BadRequestException(string message) : base(message)
    {}
}

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {}
}

public class UnauthorizedException : Exception
{
    public UnauthorizedException(string message) : base(message)
    {}
}

/// <summary>
/// Bir dış fiyat sağlayıcısıyla (Yahoo, Binance, TEFAS, TCMB EVDS, CoinGecko)
/// konuşurken bir şey ters gitti.
///
/// .Message TEKNİK detay taşır (sağlayıcı adı, HTTP uç noktası) ve yalnızca
/// loglanır. Kullanıcıya UserMessage gösterilir — sağlayıcı adı geçmeyen,
/// ne yapması gerektiğini söyleyen ayrı bir metin. Bu ayrım bilinçli: bir
/// hisse sembolü yanlış yazıldığında kullanıcı "Yahoo Finance" ismini
/// görmemeli, sadece sembolü kontrol etmesi gerektiğini anlamalı.
/// </summary>
public class ExternalServiceException : Exception
{
    public ExternalServiceFailureKind Kind { get; }

    /// Hata kullanıcının girdiği bir sembol/koddan kaynaklanıyorsa, o değer.
    /// UserMessage'da gösterilir; null ise mesajda sembole yer verilmez.
    public string? Symbol { get; }

    private readonly string? _userMessageOverride;

    // Ayrik tek-parametreli asiri yukleme: Activator.CreateInstance(type, "mesaj")
    // gibi reflection tabanli cagrilar (testlerde kullaniliyor) opsiyonel
    // parametreleri doldurmuyor, tam parametre sayisinda bir kurucu ariyor.
    public ExternalServiceException(string message)
        : this(message, ExternalServiceFailureKind.ProviderUnavailable)
    {}

    public ExternalServiceException(
        string message,
        ExternalServiceFailureKind kind = ExternalServiceFailureKind.ProviderUnavailable,
        string? symbol = null,
        string? userMessage = null) : base(message)
    {
        Kind = kind;
        Symbol = symbol;
        _userMessageOverride = userMessage;
    }

    public string UserMessage => _userMessageOverride ?? Kind switch
    {
        ExternalServiceFailureKind.SymbolNotFound => Symbol is null
            ? "Girdiğiniz sembol veya kod bulunamadı. Lütfen kontrol edip tekrar deneyin."
            : $"'{Symbol}' sembolü veya kodu bulunamadı. Lütfen kontrol edip tekrar deneyin.",
        ExternalServiceFailureKind.NoDataForRange =>
            "Bu varlık için seçtiğiniz zaman aralığında veri bulunamadı. Farklı bir aralık deneyebilirsiniz.",
        _ => "Güncel fiyat bilgisine şu anda ulaşılamıyor. Lütfen birkaç dakika sonra tekrar deneyin.",
    };
}

public enum ExternalServiceFailureKind
{
    /// Kullanıcının girdiği sembol/kod tanınmıyor — düzeltilebilir bir hata.
    /// Örn: "THY" yazılmış ("THYAO" olacaktı), "BTC2" gibi olmayan bir kripto.
    SymbolNotFound,

    /// Sağlayıcıya ulaşılamadı, hız sınırına takıldı veya yanıt beklenmedik
    /// biçimde geldi — kullanıcının hatası değil, geçici bir durum.
    ProviderUnavailable,

    /// Sembol geçerli ama seçilen tarih aralığında veri yok (örn. yeni halka
    /// arz olmuş bir hissede 5 yıllık grafik istemek).
    NoDataForRange,
}
/// <summary>
/// Islem premium abonelik gerektiriyor.
///
/// Ayri bir tip: istemcinin bunu diger 400'lerden ayirt edip hata mesaji
/// yerine paywall ekrani acabilmesi gerekiyor. HTTP 402 (Payment Required)
/// tam olarak bu durum icin ayrilmis bir kod.
/// </summary>
public class PremiumRequiredException : Exception
{
    public PremiumRequiredException(string message) : base(message)
    {}
}
