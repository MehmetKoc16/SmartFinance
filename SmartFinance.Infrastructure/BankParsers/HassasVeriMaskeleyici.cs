using System.Text.RegularExpressions;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// Ekstre açıklamalarındaki hesap numaralarını maskeler.
///
/// Neden gerekli: banka ekstresindeki havale/EFT satırları karşı tarafın
/// IBAN'ını içeriyor ve açıklama metni olduğu gibi kaydediliyordu. Bu, o
/// kişinin verisi — bizim kullanıcımızın değil. Onun rızası yok, bizim de
/// saklamak için bir gerekçemiz yok; KVKK'nın veri minimizasyonu ilkesi
/// tam olarak bunu söylüyor.
///
/// Ayrıca Play "Veri güvenliği" formunda "ödeme bilgisi topluyor muyuz"
/// sorusuna dürüstçe hayır diyebilmemizi sağlıyor.
///
/// Açıklamanın tamamı silinmiyor: kullanıcı işlemi tanıyabilmeli. Yalnızca
/// hesap numarası kısmı kırpılıyor, metnin gerisi duruyor.
/// </summary>
public static class HassasVeriMaskeleyici
{
    // TR IBAN: TR + 24 rakam. Ekstrelerde dörderli gruplar halinde boşluklu
    // da yazilabildigi icin aradaki bosluklara izin veriliyor.
    private static readonly Regex Iban = new(
        // Son karakterin RAKAM olmasi sart: aksi halde desen 24. rakamdan sonraki
        // boslugu da yutuyor ve "IBAN AHMET" yerine "IBANAHMET" cikiyordu.
        @"\bTR\s?(?:\d\s?){23}\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Maskelenmemis uzun hesap/kart numaralari. 10 hane siniri bilerek
    // yuksek: tarih (8), tutar ve referans kodlari yanlislikla silinmesin.
    private static readonly Regex UzunNumara = new(@"\b\d{10,}\b", RegexOptions.Compiled);

    private static readonly Regex FazlaBosluk = new(@"\s{2,}", RegexOptions.Compiled);

    public static string Maskele(string? metin)
    {
        if (string.IsNullOrWhiteSpace(metin)) return string.Empty;

        var sonuc = Iban.Replace(metin, "IBAN");
        sonuc = UzunNumara.Replace(sonuc, "***");

        return FazlaBosluk.Replace(sonuc, " ").Trim();
    }
}
