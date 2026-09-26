using System.Globalization;
using System.Text.RegularExpressions;
using SmartFinance.Application.DTOs.PdfImport;

namespace SmartFinance.Infrastructure.BankParsers;

public sealed record LayoutParseResult(bool HeaderFound, List<ParsedTransactionDto> Transactions);

/// <summary>
/// Bankadan bagimsiz ekstre ayristiricisi: duz metin yerine kelimelerin sayfadaki
/// KONUMUNA bakar. Girdi PDF'in gomulu metni de olabilir, telefondaki OCR ciktisi da.
///
///   1. Kelimeler dikey merkezlerine gore satirlara toplanir (toleransli; eski
///      yontem konumu birebir yuvarliyordu, 1-2 birim kaymis tutar ayri satira
///      dusuyordu).
///   2. Tablo basligi bulunur (Tarih + Borc/Alacak/Tutar/Bakiye); her sutunun
///      yatay konumu kaydedilir. Baslik her sayfada yenilenir.
///   3. Tarihle baslayan satir islemdir. Her tutar en yakin sutuna atanir: Borc
///      sutunu gider, Alacak gelir, tek Tutar sutununda isaret belirler. Eski
///      genel ayristirici turu yalnizca isaretten cikariyordu; ayri Borc/Alacak
///      sutunlu bankalarda tum giderler gelir sayiliyordu.
///   4. Tarihsiz satirlar EN YAKIN tarihli satira baglanir (ustte ya da altta):
///      bazi bankalar cok satirli hucrede tarihi dikey ortaliyor, tutar ve
///      aciklamanin basi tarihin ustundeki satirda kaliyor.
///   5. Bakiye sutunu varsa zincir kontrol edilir; tutmayan satir isaretlenir.
///
/// Baslik bulunamazsa tahmin yurutulmez (HeaderFound=false); cagiran eski metin
/// tabanli ayristiriciya duser.
/// </summary>
public sealed class LayoutStatementParser
{
    public const string BankName = "Genel (sütun tabanlı)";

    private enum Role { Date, Description, Debit, Credit, Amount, Balance, Reference }

    private static readonly Role[] MoneyRoles = [Role.Debit, Role.Credit, Role.Amount, Role.Balance];

    private static readonly (Role Role, string[] Keys)[] HeaderKeys =
    [
        (Role.Date, ["tarih", "tarihi"]),
        (Role.Description, ["aciklama", "aciklamasi", "detay", "ayrinti"]),
        (Role.Debit, ["borc", "cikan", "cikis"]),
        (Role.Credit, ["alacak", "giren", "giris"]),
        (Role.Amount, ["tutar", "tutari", "miktar"]),
        (Role.Balance, ["bakiye", "bakiyesi"]),
        // Fis/dekont numarasi sutunu: aciklamaya girmez.
        (Role.Reference, ["fis", "dekont", "referans"]),
    ];

    private static readonly Regex DateRx = new(@"^(\d{2})[./-](\d{2})[./-](\d{4}|\d{2})$", RegexOptions.Compiled);
    private static readonly Regex AmountRx = new(@"^[+-]?\(?(\d{1,3}(\.\d{3})+|\d+),\d{2}\)?(TL|₺)?$", RegexOptions.Compiled);
    // OCR ondalik virgulu noktaya cevirebiliyor ("4.100.50"). Yalnizca binlik
    // grubu varsa kabul: "12.09" gibi gun.ay parcalari tutar sanilmasin.
    private static readonly Regex OcrAmountRx = new(@"^[+-]?(\d{1,3}(\.\d{3})+)\.(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"^\d{2}:\d{2}(:\d{2})?$", RegexOptions.Compiled);
    private static readonly Regex SiraNoRx = new(@"^\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex LongDigitsRx = new(@"^\d{5,}$", RegexOptions.Compiled);
    private static readonly HashSet<string> CurrencyTokens = new(StringComparer.OrdinalIgnoreCase) { "TL", "TRY", "₺" };

    private sealed class Line
    {
        public int Page;
        public List<PositionedWord> Words = new();
        public double CenterY;
        public double Top => Words.Min(w => w.Top);
        public double Bottom => Words.Max(w => w.Bottom);
        public double Height => Words.Average(w => w.Height);
    }

    private sealed record Row(ParsedTransactionDto Tx, decimal? Balance);

    public LayoutParseResult Parse(IReadOnlyList<PositionedWord> words)
    {
        // 1. Baslik altindaki satirlar, her biri o sayfanin basligiyla.
        Dictionary<Role, PositionedWord>? header = null;
        var headerFound = false;
        var tablo = new List<(Line Line, Dictionary<Role, PositionedWord> Header)>();
        foreach (var line in GroupLines(words))
        {
            var yeniBaslik = TryHeader(line);
            if (yeniBaslik != null)
            {
                header = yeniBaslik;
                headerFound = true;
                continue;
            }
            if (header != null) tablo.Add((line, header));
        }

        // 2. Tarihsiz satirlar en yakin tarihli satira baglanir. Sayfa alti gibi
        //    uzaktaki yazilar hicbir isleme baglanmaz.
        var tarihli = tablo.Where(t => HasDate(t.Line)).ToList();
        var ekler = tarihli.ToDictionary(t => t.Line, _ => new List<Line>());
        foreach (var (line, h) in tablo.Where(t => !HasDate(t.Line)))
        {
            // Olcu satir KUTULARI arasindaki bosluk: PdfPig kutulari harflere siki
            // oturtuyor, merkez uzakligi kelime yuksekligine gore hep buyuk kaliyordu.
            var hedef = tarihli
                .Where(t => t.Line.Page == line.Page && ReferenceEquals(t.Header, h))
                .Select(t => (t.Line, Bosluk: Bosluk(t.Line, line), Merkez: Math.Abs(t.Line.CenterY - line.CenterY)))
                .Where(x => x.Bosluk <= 1.5 * Math.Max(x.Line.Height, line.Height))
                // Esitlikte ustteki: klasik "alt satira tasan aciklama".
                .OrderBy(x => x.Bosluk).ThenBy(x => x.Merkez).ThenBy(x => x.Line.CenterY)
                .Select(x => x.Line)
                .FirstOrDefault();
            if (hedef != null) ekler[hedef].Add(line);
        }

        var rows = new List<Row>();
        foreach (var (line, h) in tarihli)
        {
            var row = TryRow(line, ekler[line], h);
            if (row != null) rows.Add(row);
        }

        MarkBalanceMismatches(rows);
        return new LayoutParseResult(headerFound, rows.Select(r => r.Tx).ToList());
    }

    // Iki satirin kutulari arasindaki dikey bosluk; ust uste biniyorsa 0.
    private static double Bosluk(Line a, Line b) => Math.Max(0, Math.Max(a.Top, b.Top) - Math.Min(a.Bottom, b.Bottom));

    private static bool HasDate(Line line) => line.Words.Any(w => ParseDate(w.Text) != null);

    private static List<Line> GroupLines(IReadOnlyList<PositionedWord> words)
    {
        var lines = new List<Line>();
        foreach (var sayfa in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).GroupBy(w => w.Page).OrderBy(g => g.Key))
        {
            Line? current = null;
            foreach (var w in sayfa.OrderBy(w => w.CenterY))
            {
                if (current != null && Math.Abs(w.CenterY - current.CenterY) <= 0.6 * Math.Max(w.Height, current.Height))
                {
                    current.Words.Add(w);
                    current.CenterY = current.Words.Average(x => x.CenterY);
                }
                else
                {
                    current = new Line { Page = sayfa.Key, CenterY = w.CenterY };
                    current.Words.Add(w);
                    lines.Add(current);
                }
            }
        }
        foreach (var l in lines) l.Words.Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }

    private static Dictionary<Role, PositionedWord>? TryHeader(Line line)
    {
        if (line.Words.Any(w => ParseDate(w.Text) != null)) return null;

        var roller = new Dictionary<Role, PositionedWord>();
        foreach (var w in line.Words)
        {
            var n = Normalize(w.Text);
            foreach (var (role, keys) in HeaderKeys)
                if (!roller.ContainsKey(role) && keys.Contains(n))
                    roller[role] = w;
        }
        return roller.ContainsKey(Role.Date) && MoneyRoles.Any(roller.ContainsKey) ? roller : null;
    }

    private static Row? TryRow(Line line, List<Line> ekler, Dictionary<Role, PositionedWord> header)
    {
        var tarihKelimesi = line.Words.First(w => ParseDate(w.Text) != null);
        var paraSutunlari = MoneyRoles.Where(header.ContainsKey).ToList();

        // Tutarlar once tarihli satirdan. Orada hic tutar yoksa (tarih dikey
        // ortalanmis hucre) bagli satirlardan; tutarli bir ek satir (toplam,
        // devir) tutari olan bir isleme karismaz.
        var degerler = Tutarlar(line.Words, paraSutunlari, header);
        var kullanilanEkler = ekler.Where(e => !e.Words.Any(w => IsAmount(w.Text))).ToList();
        if (degerler.Count == 0)
        {
            kullanilanEkler = ekler;
            foreach (var e in ekler)
                foreach (var (rol, d) in Tutarlar(e.Words, paraSutunlari, header))
                    if (!degerler.TryGetValue(rol, out var mevcut) || d.Distance < mevcut.Distance)
                        degerler[rol] = d;
        }

        decimal tutar;
        int tur;
        if (degerler.TryGetValue(Role.Debit, out var borc) && borc.Value != 0)
            (tutar, tur) = (Math.Abs(borc.Value), 2);
        else if (degerler.TryGetValue(Role.Credit, out var alacak) && alacak.Value != 0)
            (tutar, tur) = (Math.Abs(alacak.Value), 1);
        else if (degerler.TryGetValue(Role.Amount, out var isaretli) && isaretli.Value != 0)
            (tutar, tur) = (Math.Abs(isaretli.Value), isaretli.Value < 0 ? 2 : 1);
        else
            return null;

        // Aciklama satirlarin yukaridan asagi sirasiyla. Tarihin solundaki kisa
        // tam sayi "Sira No" sutunudur, aciklamaya girmez.
        var aciklama = string.Join(" ", kullanilanEkler.Append(line)
            .OrderBy(l => l.CenterY)
            .Select(l => DescriptionText(l.Words.Where(w =>
                !(w.Right <= tarihKelimesi.Left && SiraNoRx.IsMatch(w.Text.Trim()))
                && !ReferansSutununda(w, header))))
            .Where(t => t.Length > 0));
        var tx = new ParsedTransactionDto
        {
            TransactionDate = ParseDate(tarihKelimesi.Text)!.Value,
            Amount = tutar,
            Type = tur,
            Description = aciklama,
            MerchantName = Merchant(aciklama),
        };
        return new Row(tx, degerler.TryGetValue(Role.Balance, out var bakiye) ? bakiye.Value : null);
    }

    /// Satirdaki tutarlar, her biri en yakin para sutununa.
    private static Dictionary<Role, (decimal Value, double Distance)> Tutarlar(
        List<PositionedWord> kelimeler, List<Role> paraSutunlari, Dictionary<Role, PositionedWord> header)
    {
        var degerler = new Dictionary<Role, (decimal Value, double Distance)>();
        for (var i = 0; i < kelimeler.Count; i++)
        {
            var w = kelimeler[i];
            if (!IsAmount(w.Text)) continue;

            var deger = ParseAmount(w.Text);
            // Ayri yazilmis isaret ("-" 75,25): hemen solundaki tek karakterlik kelime.
            if (i > 0 && kelimeler[i - 1].Text is "-" or "+" && w.Left - kelimeler[i - 1].Right <= 2 * w.Height)
                deger = kelimeler[i - 1].Text == "-" ? -Math.Abs(deger) : Math.Abs(deger);

            var (rol, uzaklik) = paraSutunlari
                .Select(r => (r, Distance(w, header[r])))
                .OrderBy(x => x.Item2)
                .First();
            if (!degerler.TryGetValue(rol, out var mevcut) || uzaklik < mevcut.Distance)
                degerler[rol] = (deger, uzaklik);
        }
        return degerler;
    }

    // Sutun basligi ile deger ayni hizada olmayabilir (sola/saga/ortaya yasli);
    // uc hizalamadan en yakini alinir.
    private static double Distance(PositionedWord value, PositionedWord header) => Math.Min(
        Math.Abs(value.CenterX - header.CenterX),
        Math.Min(Math.Abs(value.Right - header.Right), Math.Abs(value.Left - header.Left)));

    private static string DescriptionText(IEnumerable<PositionedWord> words) => string.Join(" ", words
        .Select(w => w.Text.Trim())
        .Where(t => t.Length > 0
            && ParseDate(t) == null && !IsAmount(t) && !TimeRx.IsMatch(t) && !LongDigitsRx.IsMatch(t)
            && t is not "-" and not "+" && !CurrencyTokens.Contains(t)));

    // Isyeri adi ("ISYERI: X MUTABAKAT", "Gond: X", "-X/FAST islemi"); ogrenilen
    // kategori eslesmeleri buna dayaniyor. Ilk 50 karakter tum kart
    // harcamalarinda ayni "POS ALISVERIS KART NO..." oluyordu.
    private static string Merchant(string description) =>
        ZiraatParser.ExtractMerchantName(description) ?? description;

    private static bool ReferansSutununda(PositionedWord w, Dictionary<Role, PositionedWord> header) =>
        header.TryGetValue(Role.Reference, out var referans)
        && header.TryGetValue(Role.Description, out var aciklama)
        && Distance(w, referans) < Distance(w, aciklama);

    /// <summary>
    /// Her satirin bakiyesi = onceki satirin bakiyesi +/- bu satirin tutari. Ekstre
    /// eskiden yeniye veya yeniden eskiye sirali olabilir; daha az celiski veren
    /// yon dogru kabul edilir. Yalnizca tum satirlarda bakiye varsa uygulanir.
    /// </summary>
    private static void MarkBalanceMismatches(List<Row> rows)
    {
        if (rows.Count < 2 || rows.Any(r => r.Balance == null)) return;

        decimal Isaretli(int i) => rows[i].Tx.Type == 1 ? rows[i].Tx.Amount : -rows[i].Tx.Amount;
        bool Tutmuyor(decimal beklenen, decimal gercek) => Math.Abs(beklenen - gercek) > 0.01m;

        var eskidenYeniye = Enumerable.Range(1, rows.Count - 1)
            .Where(i => Tutmuyor(rows[i - 1].Balance!.Value + Isaretli(i), rows[i].Balance!.Value)).ToList();
        var yenidenEskiye = Enumerable.Range(0, rows.Count - 1)
            .Where(i => Tutmuyor(rows[i + 1].Balance!.Value + Isaretli(i), rows[i].Balance!.Value)).ToList();

        foreach (var i in eskidenYeniye.Count <= yenidenEskiye.Count ? eskidenYeniye : yenidenEskiye)
            rows[i].Tx.BalanceMismatch = true;
    }

    // OCR tablo cizgisini kelimeye yapistirabiliyor ("|21.09.2026").
    private static string Temiz(string text) => text.Trim().Trim('|', '[', ']', '{', '}', '`', '\'', '"').Trim();

    private static bool IsAmount(string text) => AmountRx.IsMatch(Temiz(text)) || OcrAmountRx.IsMatch(Temiz(text));

    private static decimal ParseAmount(string text)
    {
        var s = Temiz(text);
        // "4.100.50" -> "4.100,50": son nokta ondalik ayirici.
        if (OcrAmountRx.IsMatch(s)) s = s[..s.LastIndexOf('.')] + "," + s[(s.LastIndexOf('.') + 1)..];
        var eksi = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Replace("TL", "").Replace("₺", "").Trim('+', '-', '(', ')').Replace(".", "").Replace(",", ".");
        var deger = decimal.Parse(s, CultureInfo.InvariantCulture);
        return eksi ? -deger : deger;
    }

    private static DateTime? ParseDate(string text)
    {
        var m = DateRx.Match(Temiz(text));
        if (!m.Success) return null;
        var bicim = m.Groups[3].Value.Length == 4 ? "dd.MM.yyyy" : "dd.MM.yy";
        var duz = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
        return DateTime.TryParseExact(duz, bicim, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string Normalize(string s)
    {
        var k = s.Trim().Trim(':', '.', '(', ')').ToLower(new CultureInfo("tr-TR"));
        return k.Replace('ç', 'c').Replace('ğ', 'g').Replace('ı', 'i').Replace('ö', 'o')
                .Replace('ş', 's').Replace('ü', 'u').Replace("i̇", "i");
    }
}
