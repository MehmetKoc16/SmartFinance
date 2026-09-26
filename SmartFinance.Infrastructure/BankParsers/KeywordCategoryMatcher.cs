using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// Ekstre aciklamasindan varsayilan kategori adaylari. Anahtar kelime KELIME
/// olarak aranir; sonuna yalnizca yaygin Turkce ekler gelebilir (FATURA-SI,
/// MARKET-I, BALIK-CI). Eskiden metnin icinde parca araniyordu: BALIK -> BALIKESIR,
/// SOK -> SOKAK, ATM -> ATMACA eslesiyordu (26.09.2026, gercek Ziraat ekstresi).
///
/// Adaylar liste sirasiyla doner; turu (gelir/gider) uyan ve kullanicida var
/// olan ilk kategoriyi secmek cagirana ait. Ayni anahtar iki kategoriye
/// gidebilir: "ucret" gelirde maas, giderde banka masrafi.
///
/// Kategori adlari AuthService'te her yeni kullaniciya acilan varsayilan
/// kategorilerle hizali tutulmali.
/// </summary>
public static class KeywordCategoryMatcher
{
    // Katlanmis (aksansiz, buyuk harf) anahtar -> kategori adi.
    private static readonly (string Keyword, string Category)[] Keywords =
    [
        // Maaş
        ("MAAS", "Maaş"),
        ("UCRET", "Maaş"),
        ("AYLIK", "Maaş"),
        // Ek Gelir ("BURS" tek basina BURSA sehrine de uyardi)
        ("BURS BEDELI", "Ek Gelir"),
        // Kira Geliri (yalnizca gelirde; giderdeki kira icin varsayilan kategori yok)
        ("KIRA", "Kira Geliri"),
        // Transfer
        ("EFT", "Transfer"),
        ("HAVALE", "Transfer"),
        ("FAST", "Transfer"),
        ("VIRMAN", "Transfer"),
        // ATM
        ("ATM", "ATM"),
        // Fatura
        ("FATURA", "Fatura"),
        ("ELEKTRIK", "Fatura"),
        ("DOGALGAZ", "Fatura"),
        ("SU FATURA", "Fatura"),
        ("INTERNET", "Fatura"),
        ("TELEFON", "Fatura"),
        ("TURKCELL", "Fatura"),
        ("VODAFONE", "Fatura"),
        ("TURK TELEKOM", "Fatura"),
        ("BSMV", "Fatura"),
        ("KOMISYON", "Fatura"),
        ("MASRAF", "Fatura"),
        // Giderde "ucret" banka masrafidir (mesaj, hesap isletim ucreti).
        ("UCRET", "Fatura"),
        // Yeme-İçme
        ("YEMEK", "Yeme-İçme"),
        ("RESTORAN", "Yeme-İçme"),
        ("RESTAURANT", "Yeme-İçme"),
        ("CAFE", "Yeme-İçme"),
        ("KAFE", "Yeme-İçme"),
        ("LOKANTA", "Yeme-İçme"),
        ("PIDE", "Yeme-İçme"),
        ("KEBAP", "Yeme-İçme"),
        ("KEBAB", "Yeme-İçme"),
        ("SIMIT", "Yeme-İçme"),
        ("PASTANE", "Yeme-İçme"),
        ("FIRIN", "Yeme-İçme"),
        ("BALIK", "Yeme-İçme"),
        ("PIZZA", "Yeme-İçme"),
        ("BURGER", "Yeme-İçme"),
        ("COFFEE", "Yeme-İçme"),
        ("KAHVE", "Yeme-İçme"),
        ("KANTIN", "Yeme-İçme"),
        ("TIKLAGELSIN", "Yeme-İçme"),
        ("STARBUCKS", "Yeme-İçme"),
        ("MCDONALD", "Yeme-İçme"),
        ("KFC", "Yeme-İçme"),
        ("DOMINO", "Yeme-İçme"),
        ("SUBWAY", "Yeme-İçme"),
        ("YEMEKSEPETI", "Yeme-İçme"),
        ("GETIR YEMEK", "Yeme-İçme"),
        ("TRENDYOL YEMEK", "Yeme-İçme"),
        ("CIKOLATA", "Yeme-İçme"),
        // Ulaşım
        ("BENZIN", "Ulaşım"),
        ("PETROL", "Ulaşım"),
        ("AKARYAKIT", "Ulaşım"),
        ("OPET", "Ulaşım"),
        ("SHELL", "Ulaşım"),
        ("BP", "Ulaşım"),
        ("TOTAL ENERJI", "Ulaşım"),
        ("OTOPARK", "Ulaşım"),
        ("TAKSI", "Ulaşım"),
        ("TAXI", "Ulaşım"),
        ("UBER", "Ulaşım"),
        ("BITAKSI", "Ulaşım"),
        ("METROBUS", "Ulaşım"),
        ("OTOBUS", "Ulaşım"),
        ("AKBIL", "Ulaşım"),
        ("ISTANBULKART", "Ulaşım"),
        ("THY", "Ulaşım"),
        ("PEGASUS", "Ulaşım"),
        ("SUNEXPRESS", "Ulaşım"),
        ("OBILET", "Ulaşım"),
        // Alışveriş
        ("MIGROS", "Alışveriş"),
        ("BIM", "Alışveriş"),
        ("A101", "Alışveriş"),
        ("SOK", "Alışveriş"),
        ("CARREFOUR", "Alışveriş"),
        ("MARKET", "Alışveriş"),
        ("MAGAZA", "Alışveriş"),
        ("TEKNOSA", "Alışveriş"),
        ("MEDIAMARKT", "Alışveriş"),
        ("LCW", "Alışveriş"),
        ("DEFACTO", "Alışveriş"),
        ("KOTON", "Alışveriş"),
        ("TRENDYOL", "Alışveriş"),
        ("HEPSIBURADA", "Alışveriş"),
        ("AMAZON", "Alışveriş"),
        ("N11", "Alışveriş"),
        ("GIDA", "Alışveriş"),
        ("TEKEL", "Alışveriş"),
        ("PLAYSTATION", "Alışveriş"),
        ("ELEKTRONIK", "Alışveriş"),
    ];

    // Kelimenin sonuna gelebilecek ekler (katlanmis). "BALIK" + "ESIR" gibi
    // baska bir kelimeye donusen devamlar burada yok.
    private static readonly HashSet<string> Ekler =
    [
        "", "I", "U", "SI", "SU", "YI", "YU", "IN", "UN", "NIN", "NUN",
        "DE", "DA", "TE", "TA", "DEN", "DAN", "TEN", "TAN", "NDE", "NDA",
        "LAR", "LER", "LARI", "LERI", "CI", "CU", "LI", "LU", "E", "A", "YE", "YA", "S",
        // Iyelik "-m": isyeri adlarinda yaygin (PIDEM, KEBABIM, DONERIM).
        "M", "IM", "UM",
    ];

    private static readonly (string[] Tokens, string Category)[] Hazir =
        Keywords.Select(k => (k.Keyword.Split(' '), k.Category)).ToArray();

    public static IEnumerable<string> Candidates(string text)
    {
        var kelimeler = Tokens(text);
        foreach (var (anahtar, kategori) in Hazir)
            if (Iceriyor(kelimeler, anahtar))
                yield return kategori;

        // IBAN kesin havale isareti. Bankalar "ucretiFAST islemi" diye bitisik
        // yazdigi icin FAST kelimesi yakalanamiyor; OCR IBAN'i ikiye bolebiliyor.
        if (kelimeler.Any(k => IbanRx.IsMatch(k)))
            yield return "Transfer";
    }

    private static readonly Regex IbanRx = new(@"^TR\d{10,24}$", RegexOptions.Compiled);

    private static bool Iceriyor(string[] kelimeler, string[] anahtar)
    {
        for (var i = 0; i + anahtar.Length <= kelimeler.Length; i++)
        {
            var tamam = true;
            for (var j = 0; j < anahtar.Length && tamam; j++)
            {
                var k = kelimeler[i + j];
                var a = anahtar[j];
                // Cok kelimeli anahtarda yalnizca son kelime ek alabilir.
                tamam = j < anahtar.Length - 1
                    ? k == a
                    : k.StartsWith(a, StringComparison.Ordinal) && Ekler.Contains(k[a.Length..]);
            }
            if (tamam) return true;
        }
        return false;
    }

    private static readonly CultureInfo Tr = new("tr-TR");
    private static readonly Regex AyiriciRx = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static string[] Tokens(string text)
    {
        var buyuk = text.ToUpper(Tr)
            .Replace('Ç', 'C').Replace('Ğ', 'G').Replace('İ', 'I').Replace('Ö', 'O').Replace('Ş', 'S').Replace('Ü', 'U');
        return AyiriciRx.Split(buyuk).Where(k => k.Length > 0).ToArray();
    }
}
