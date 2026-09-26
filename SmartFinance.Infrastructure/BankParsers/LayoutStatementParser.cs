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
///   4. Tarihsiz, hemen alttaki satirlar onceki islemin aciklamasina eklenir.
///   5. Bakiye sutunu varsa zincir kontrol edilir; tutmayan satir isaretlenir.
///
/// Baslik bulunamazsa tahmin yurutulmez (HeaderFound=false); cagiran eski metin
/// tabanli ayristiriciya duser.
/// </summary>
public sealed class LayoutStatementParser
{
    public const string BankName = "Genel (sütun tabanlı)";

    private enum Role { Date, Description, Debit, Credit, Amount, Balance }

    private static readonly Role[] MoneyRoles = [Role.Debit, Role.Credit, Role.Amount, Role.Balance];

    private static readonly (Role Role, string[] Keys)[] HeaderKeys =
    [
        (Role.Date, ["tarih", "tarihi"]),
        (Role.Description, ["aciklama", "aciklamasi", "detay", "ayrinti"]),
        (Role.Debit, ["borc", "cikan", "cikis"]),
        (Role.Credit, ["alacak", "giren", "giris"]),
        (Role.Amount, ["tutar", "tutari", "miktar"]),
        (Role.Balance, ["bakiye", "bakiyesi"]),
    ];

    private static readonly Regex DateRx = new(@"^(\d{2})[./-](\d{2})[./-](\d{4}|\d{2})$", RegexOptions.Compiled);
    private static readonly Regex AmountRx = new(@"^[+-]?\(?(\d{1,3}(\.\d{3})+|\d+),\d{2}\)?(TL|₺)?$", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"^\d{2}:\d{2}(:\d{2})?$", RegexOptions.Compiled);
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
        Dictionary<Role, PositionedWord>? header = null;
        var headerFound = false;
        var rows = new List<Row>();
        Line? lastRowLine = null;

        foreach (var line in GroupLines(words))
        {
            var yeniBaslik = TryHeader(line);
            if (yeniBaslik != null)
            {
                header = yeniBaslik;
                headerFound = true;
                lastRowLine = null;
                continue;
            }
            if (header == null) continue;

            var row = TryRow(line, header);
            if (row != null)
            {
                rows.Add(row);
                lastRowLine = line;
                continue;
            }

            // Tarihsiz satir: hemen ustteki islemin aciklamasinin devami mi?
            // Sayfa alti ("Sayfa 1/2") gibi uzaktaki yazilar eklenmez.
            if (lastRowLine != null && line.Page == lastRowLine.Page
                && line.Top - lastRowLine.Bottom <= 1.5 * lastRowLine.Height
                && !line.Words.Any(w => IsAmount(w.Text)))
            {
                var ek = DescriptionText(line.Words);
                if (ek.Length > 0)
                {
                    var tx = rows[^1].Tx;
                    tx.Description = (tx.Description + " " + ek).Trim();
                    tx.MerchantName = Merchant(tx.Description);
                }
                lastRowLine = line;
            }
            else
            {
                lastRowLine = null;
            }
        }

        MarkBalanceMismatches(rows);
        return new LayoutParseResult(headerFound, rows.Select(r => r.Tx).ToList());
    }

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

    private static Row? TryRow(Line line, Dictionary<Role, PositionedWord> header)
    {
        var tarihKelimesi = line.Words.FirstOrDefault(w => ParseDate(w.Text) != null);
        if (tarihKelimesi == null) return null;

        var paraSutunlari = MoneyRoles.Where(header.ContainsKey).ToList();
        var degerler = new Dictionary<Role, (decimal Value, double Distance)>();

        for (var i = 0; i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            if (!IsAmount(w.Text)) continue;

            var deger = ParseAmount(w.Text);
            // Ayri yazilmis isaret ("-" 75,25): hemen solundaki tek karakterlik kelime.
            if (i > 0 && line.Words[i - 1].Text is "-" or "+" && w.Left - line.Words[i - 1].Right <= 2 * w.Height)
                deger = line.Words[i - 1].Text == "-" ? -Math.Abs(deger) : Math.Abs(deger);

            var (rol, uzaklik) = paraSutunlari
                .Select(r => (r, Distance(w, header[r])))
                .OrderBy(x => x.Item2)
                .First();
            if (!degerler.TryGetValue(rol, out var mevcut) || uzaklik < mevcut.Distance)
                degerler[rol] = (deger, uzaklik);
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

        var aciklama = DescriptionText(line.Words);
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

    private static string Merchant(string description) =>
        description.Length > 50 ? description[..50].Trim() : description;

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

    private static bool IsAmount(string text) => AmountRx.IsMatch(text.Trim());

    private static decimal ParseAmount(string text)
    {
        var s = text.Trim();
        var eksi = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Replace("TL", "").Replace("₺", "").Trim('+', '-', '(', ')').Replace(".", "").Replace(",", ".");
        var deger = decimal.Parse(s, CultureInfo.InvariantCulture);
        return eksi ? -deger : deger;
    }

    private static DateTime? ParseDate(string text)
    {
        var m = DateRx.Match(text.Trim());
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
