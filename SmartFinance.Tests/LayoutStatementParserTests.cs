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

    /// Regresyon (26.09.2026, gercek Garanti ekstresi): cok satirli hucrede tarih
    /// dikey ORTALANIYOR; aciklamanin ilk satiri ve tutar tarihin USTUNDEKI
    /// satirda. Tarihli satirda tutar bulunamadigi icin gelen havale (maas gibi
    /// buyuk bir gelir) sessizce atlaniyordu.
    [Fact]
    public void DikeyOrtalanmisTarih_UstSatirdakiTutarVeAciklamaAlinir()
    {
        var baslik = new[] { ("Tarih", 31.0), ("Açıklama", 88.0), ("Etiket", 315.0), ("Tutar", 460.0), ("Bakiye", 541.0) };
        var sonuc = Coz(Satir(221, baslik)
            .Concat(Satir(237, ("04.09.2026", 31), ("HAVALE", 88), ("GIDEN", 125), ("Transfer", 315), ("-1.111,11", 440), ("0,00", 544)))
            .Concat(Satir(251, ("04.09.2026", 31), ("KART", 88), ("ODEMESI", 115), ("Kart", 315), ("-2.222,22", 436), ("1.111,11", 528)))
            .Concat(Satir(265, ("ORNEK", 88), ("KURUMU", 118), ("GELEN", 160), ("+3.333,33", 434), ("3.333,33", 524)))
            .Concat(Satir(274, ("04.09.2026", 31), ("KURUM", 88), ("ADINA", 125), ("Transfer", 315)))
            .Concat(Satir(284, ("GONDERILEN", 88))));

        Assert.Equal([(2, 1111.11m), (2, 2222.22m), (1, 3333.33m)], sonuc.Transactions.Select(t => (t.Type, t.Amount)));
        Assert.Equal("ORNEK KURUMU GELEN KURUM ADINA Transfer GONDERILEN", sonuc.Transactions[2].Description);
        Assert.All(sonuc.Transactions, t => Assert.False(t.BalanceMismatch)); // yeniden eskiye zincir tutuyor
    }

    /// Tutari olan tarihsiz satir (toplam, devir) tutari olan bir isleme karismaz.
    [Fact]
    public void TutarliTarihsizSatir_TutariOlanIslemeKarismaz()
    {
        var sonuc = Coz(Satir(50, BorcAlacakBasligi)
            .Concat(Satir(70, ("01.09.2026", 20), ("MARKET", 100), ("150,00", 300), ("1.850,00", 460)))
            .Concat(Satir(82, ("TOPLAM", 100), ("150,00", 300))));

        var tx = Assert.Single(sonuc.Transactions);
        Assert.Equal(150m, tx.Amount);
        Assert.Equal("MARKET", tx.Description);
    }

    /// Regresyon (26.09.2026, gercek TEB ekstresi): "Sira No" sutunundaki sayi
    /// aciklamanin basina yapisiyordu ("1 ... den gelen havale").
    [Fact]
    public void SiraNumarasi_AciklamayaKarismaz()
    {
        var sonuc = Coz(Satir(444, ("Sıra", 47), ("No", 66), ("Tarih", 121), ("Açıklama", 246), ("İşlem", 391), ("Tutarı", 417), ("Bakiye", 502))
            .Concat(Satir(472, ("1", 55), ("04.09.2026", 110), ("ORNEK", 180), ("den", 230), ("gelen", 250), ("havale", 280), ("1.111,11", 400), ("1.111,11", 500)))
            .Concat(Satir(492, ("12", 55), ("05.09.2026", 110), ("ATM", 180), ("Para", 200), ("Cekme", 225), ("-500,00", 400), ("1.000,00", 500))));

        Assert.Equal(["ORNEK den gelen havale", "ATM Para Cekme"], sonuc.Transactions.Select(t => t.Description));
    }

    /// Regresyon (26.09.2026): PdfPig kelime kutularini harflere siki oturtuyor
    /// (yukseklik ~6, satir araligi 11). Satir merkezleri arasi uzaklikla
    /// bakilinca alt satira tasan aciklama hicbir isleme baglanmiyordu.
    [Fact]
    public void SikiKutuluKelimeler_DevamSatiriYineBaglanir()
    {
        static PositionedWord K(string m, double sol, double ust) => new(m, sol, ust, sol + m.Length * 5, ust + 6);
        var sonuc = Coz(Satir(130, BorcAlacakBasligi)
            .Concat([K("04.09.2026", 20, 148.8), K("KREDI", 100, 148.8), K("KARTI", 130, 148.8), K("4.250,00", 300, 148.8), K("30.000,00", 460, 148.8)])
            .Concat([K("KART", 100, 159.8), K("SON", 125, 159.8), K("HANE", 145, 159.8)])
            .Concat([K("05.09.2026", 20, 178.8), K("ECZANE", 100, 178.8), K("312,45", 300, 178.8), K("29.687,55", 460, 178.8)]));

        Assert.Equal(["KREDI KARTI KART SON HANE", "ECZANE"], sonuc.Transactions.Select(t => t.Description));
    }

    // Ziraat duzeni: Tarih | Fis No | Aciklama (iki satir, tarih dikey ortada) | Tutar | Bakiye
    private static readonly (string, double)[] ZiraatBasligi =
        [("Tarih", 73), ("Fiş", 240), ("No", 281), ("Açıklama", 436), ("Tutar", 1427), ("Bakiye", 1592)];

    /// Regresyon (26.09.2026, gercek Ziraat taramasi, telefonda ML Kit): tablo
    /// cizgisi tarihin basina "|" olarak yapisti ("|21.09.2026"); satir islem
    /// sayilmadi ve 100 TL'lik harcama sessizce kayboldu.
    [Fact]
    public void OcrTablocizgisiYapismisTarih_YineOkunur()
    {
        var sonuc = Coz(Satir(445, ZiraatBasligi)
            .Concat(Satir(1689, ("21.09.2026", 74), ("F10001", 238), ("MARKET", 436), ("-100,00", 1428), ("1.200,00", 1591)))
            .Concat(Satir(1766, ("|21.09.2026", 74), ("F10002", 239), ("MARKET", 436), ("-100,00", 1428), ("1.300,00", 1591))));

        Assert.Equal(2, sonuc.Transactions.Count);
    }

    /// OCR ondalik virgulu noktaya cevirebiliyor ("4.100.50"). Bakiye okunamazsa
    /// zincir kontrolu tum ekstre icin kapaniyordu.
    [Fact]
    public void OcrNoktaliOndalik_BinlikAyiriciliysaTutarSayilir()
    {
        var sonuc = Coz(Satir(445, ZiraatBasligi)
            .Concat(Satir(686, ("25.09.2026", 74), ("F20001", 239), ("BSMV", 436), ("-0,38", 1428), ("4.100.50", 1591)))
            .Concat(Satir(730, ("25.09.2026", 75), ("F20001", 239), ("KOMISYON", 436), ("-7,62", 1428), ("4.101,00", 1591)))
            .Concat(Satir(790, ("24.09.2026", 74), ("F20002", 239), ("TARIH", 436), ("GIBI", 520), ("12.09", 600), ("-1,00", 1428), ("9.999,99", 1591))));

        Assert.Equal([0.38m, 7.62m, 1m], sonuc.Transactions.Select(t => t.Amount));
        // Son satirin bakiyesi bilerek yanlis. Zincir ancak "4.100.50" okunursa
        // kontrol edilir (bir satirda bakiye yoksa kontrol hic yapilmiyor):
        // 4.101,00 - 0,38 = 4.100,50 tutuyor, KOMISYON satiri tutmuyor.
        Assert.Equal([false, true, false], sonuc.Transactions.Select(t => t.BalanceMismatch));
        Assert.Contains("12.09", sonuc.Transactions[2].Description); // binlik grubu olmayan "12.09" tutar degil
    }

    /// Fis No sutunu aciklamaya girmez; isyeri adi "ISYERI: ... MUTABAKAT"
    /// arasindan alinir (ogrenilen kategori eslesmeleri buna dayaniyor; ilk 50
    /// karakter tum kart harcamalarinda ayni "POS ALISVERIS KART NO..." idi).
    [Fact]
    public void FisNoAciklamayaGirmez_IsyeriAdiAyiklanir()
    {
        var sonuc = Coz(Satir(445, ZiraatBasligi)
            .Concat(Satir(1357, ("POS", 436), ("ALIŞVERİŞ", 495), ("KART", 771), ("NO:", 851), ("9999", 906), ("İŞYERİ:", 1157)))
            .Concat(Satir(1380, ("23.09.2026", 73), ("FO9999", 239), ("-180,00", 1428), ("800,00", 1592)))
            .Concat(Satir(1393, ("ORNEK", 441), ("GIDA", 669), ("MUTABAKAT:", 750), ("1234567", 934))));

        var tx = Assert.Single(sonuc.Transactions);
        Assert.DoesNotContain("FO9999", tx.Description);
        Assert.StartsWith("POS ALIŞVERİŞ KART NO: 9999 İŞYERİ: ORNEK GIDA", tx.Description);
        Assert.Equal("ORNEK GIDA", tx.MerchantName);
    }
}
