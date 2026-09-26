using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Infrastructure.BankParsers;

namespace SmartFinance.Tests;

/// <summary>
/// Bankadan bagimsiz, kelime konumlarina dayanan ayristirici. Girdiler sentetik
/// ekstre duzenleri (gercek ekstre depoya giremez): sutun basliklarinin x
/// konumlari ve satirlar, TEB/Is Bankasi/Garanti gibi bankalarin yaygin
/// duzenlerinden esinlenildi.
/// </summary>
public class LayoutStatementParserTests
{
    // Kelime genisligi karakter basina 5 birim, yukseklik 10.
    private static PositionedWord W(string metin, double sol, double ust, int sayfa = 1) =>
        new(metin, sol, ust, sol + metin.Length * 5, ust + 10, sayfa);

    private static IEnumerable<PositionedWord> Satir(double ust, params (string Metin, double Sol)[] kelimeler) =>
        kelimeler.Select(k => W(k.Metin, k.Sol, ust));

    private static IEnumerable<PositionedWord> Satir(double ust, int sayfa, params (string Metin, double Sol)[] kelimeler) =>
        kelimeler.Select(k => W(k.Metin, k.Sol, ust, sayfa));

    private static readonly (string, double)[] BorcAlacakBasligi =
        [("Tarih", 20), ("Açıklama", 100), ("Borç", 300), ("Alacak", 380), ("Bakiye", 460)];

    private static LayoutParseResult Coz(IEnumerable<PositionedWord> kelimeler) =>
        new LayoutStatementParser().Parse(kelimeler.ToList());

    /// Eski genel ayristiricinin hatasi: Bor&ccedil; sutunundaki gider isaretsiz
    /// oldugu icin GELIR sayiliyordu. Tur sutundan belirlenmeli.
    [Fact]
    public void BorcAlacakSutunlari_TuruSutundanBelirler()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("01.09.2026", 20), ("MARKET", 100), ("ALISVERIS", 140), ("150,00", 300), ("1.850,00", 460)))
            .Concat(Satir(90, ("02.09.2026", 20), ("MAAS", 100), ("ODEMESI", 130), ("5.000,00", 380), ("6.850,00", 460))));

        Assert.True(sonuc.HeaderFound);
        Assert.Equal(2, sonuc.Transactions.Count);

        var market = sonuc.Transactions[0];
        Assert.Equal(new DateTime(2026, 9, 1), market.TransactionDate);
        Assert.Equal(150m, market.Amount);
        Assert.Equal(2, market.Type);
        Assert.Equal("MARKET ALISVERIS", market.Description);

        var maas = sonuc.Transactions[1];
        Assert.Equal(5000m, maas.Amount);
        Assert.Equal(1, maas.Type);
        Assert.False(maas.BalanceMismatch);
    }

    [Fact]
    public void TekIsaretliTutarSutunu_IsarettenTurBelirler()
    {
        var sonuc = Coz(Satir(50, ("İşlem", 20), ("Tarihi", 55), ("Açıklama", 120), ("İşlem", 320), ("Tutarı", 350), ("Bakiye", 460))
            .Concat(Satir(70, ("03.09.2026", 20), ("ELEKTRIK", 120), ("FATURASI", 165), ("-420,50", 340), ("6.429,50", 460)))
            .Concat(Satir(90, ("04.09.2026", 20), ("EFT", 120), ("GELEN", 140), ("1.000,00", 340), ("7.429,50", 460))));

        Assert.Equal(2, sonuc.Transactions.Count);
        Assert.Equal((2, 420.50m), (sonuc.Transactions[0].Type, sonuc.Transactions[0].Amount));
        Assert.Equal((1, 1000m), (sonuc.Transactions[1].Type, sonuc.Transactions[1].Amount));
        Assert.All(sonuc.Transactions, t => Assert.False(t.BalanceMismatch));
    }

    /// Eski satir gruplamasi dikey konumu birebir yuvarliyordu; ayni satirda 2
    /// birim asagida duran tutar ayri satira dusup kayboluyordu.
    [Fact]
    public void AyniSatirdaHafifKaymisKelimeler_BirlikteOkunur()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Append(W("05.09.2026", 20, 70)).Append(W("ECZANE", 100, 71)).Append(W("89,90", 300, 72.4)).Append(W("7.339,60", 460, 68.6)));

        var islem = Assert.Single(sonuc.Transactions);
        Assert.Equal(89.90m, islem.Amount);
        Assert.Equal(2, islem.Type);
    }

    /// Uzun aciklamalar bir sonraki satira tasiyor; tarihsiz satir onceki
    /// isleme eklenmeli, ayri islem sayilmamali.
    [Fact]
    public void CokSatirliAciklama_OncekiIslemeEklenir()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("06.09.2026", 20), ("KREDI", 100), ("KARTI", 130), ("ODEMESI", 160), ("2.000,00", 300), ("5.339,60", 460)))
            .Concat(Satir(82, ("SON", 100), ("4", 120), ("HANE", 130)))
            .Concat(Satir(100, ("07.09.2026", 20), ("KIRA", 100), ("3.000,00", 300), ("2.339,60", 460))));

        Assert.Equal(2, sonuc.Transactions.Count);
        Assert.Equal("KREDI KARTI ODEMESI SON 4 HANE", sonuc.Transactions[0].Description);
    }

    /// Bakiye zinciri tutmayan satir isaretlenir: tutar ya da tur yanlis
    /// okunmus olabilir (ozellikle OCR'da).
    [Fact]
    public void BakiyeZinciriTutmazsa_SatirIsaretlenir()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("01.09.2026", 20), ("A", 100), ("100,00", 300), ("900,00", 460)))
            .Concat(Satir(90, ("02.09.2026", 20), ("B", 100), ("200,00", 300), ("750,00", 460)))
            .Concat(Satir(110, ("03.09.2026", 20), ("C", 100), ("50,00", 300), ("700,00", 460))));

        Assert.Equal([false, true, false], sonuc.Transactions.Select(t => t.BalanceMismatch));
    }

    /// Bazi bankalar en yeni islemi en uste yaziyor; zincir ters yonde de
    /// kontrol edilmeli, dogru satirlar isaretlenmemeli.
    [Fact]
    public void YenidenEskiyeSiraliEkstre_BakiyeZinciriniDogruKontrolEder()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("03.09.2026", 20), ("C", 100), ("50,00", 300), ("650,00", 460)))
            .Concat(Satir(90, ("02.09.2026", 20), ("B", 100), ("200,00", 300), ("700,00", 460)))
            .Concat(Satir(110, ("01.09.2026", 20), ("A", 100), ("100,00", 300), ("900,00", 460))));

        Assert.All(sonuc.Transactions, t => Assert.False(t.BalanceMismatch));
    }

    /// Baslik her sayfada tekrar ediyor; sayfa altindaki "Sayfa 1/2" gibi
    /// yazilar aciklamaya eklenmemeli.
    [Fact]
    public void IkinciSayfadakiSatirlarOkunur_SayfaAltiYazisiEklenmez()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("01.09.2026", 20), ("A", 100), ("100,00", 300), ("900,00", 460)))
            .Concat(Satir(780, ("Sayfa", 250), ("1/2", 285)))
            .Concat(Satir(50, 2, BorcAlacakBasligi))
            .Concat(Satir(70, 2, ("02.09.2026", 20), ("B", 100), ("200,00", 300), ("700,00", 460))));

        Assert.Equal(2, sonuc.Transactions.Count);
        Assert.Equal("A", sonuc.Transactions[0].Description);
        Assert.Equal(200m, sonuc.Transactions[1].Amount);
    }

    [Fact]
    public void AyriEksiIsaretiTutaraUygulanir()
    {
        var sonuc = Coz(Satir(50, ("Tarih", 20), ("Açıklama", 100), ("Tutar", 340), ("Bakiye", 460))
            .Concat(Satir(70, ("08.09.2026", 20), ("SU", 100), ("FATURASI", 115), ("-", 333), ("75,25", 340), ("924,75", 460))));

        var islem = Assert.Single(sonuc.Transactions);
        Assert.Equal((2, 75.25m), (islem.Type, islem.Amount));
    }

    /// Baslik bulunamazsa ayristirici tahmin yurutmez; servis eski metin
    /// tabanli ayristiriciya duser.
    [Fact]
    public void BaslikYoksa_HeaderFoundFalse()
    {
        var sonuc = Coz(Satir(70, ("01.09.2026", 20), ("A", 100), ("100,00", 300)));

        Assert.False(sonuc.HeaderFound);
        Assert.Empty(sonuc.Transactions);
    }
}
