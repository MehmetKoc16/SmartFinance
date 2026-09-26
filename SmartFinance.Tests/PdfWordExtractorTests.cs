using SmartFinance.Infrastructure.BankParsers;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartFinance.Tests;

/// Gercek bir PDF uretip kelimeleri cikarir ve ayristiricidan gecirir: PdfPig'in
/// koordinatlari alttan yukari, ayristiricininki ustten asagi. Cevirme ters
/// yapilirsa baslik satirlarin ALTINDA kalir ve hicbir islem okunmaz.
public class PdfWordExtractorTests
{
    private static byte[] OrnekEkstre()
    {
        var builder = new PdfDocumentBuilder();
        var sayfa = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        void Yaz(string metin, double x, double yUstten) =>
            sayfa.AddText(metin, 9, new PdfPoint(x, 842 - yUstten), font);

        Yaz("Tarih", 40, 100); Yaz("Aciklama", 120, 100); Yaz("Borc", 330, 100); Yaz("Alacak", 410, 100); Yaz("Bakiye", 490, 100);
        Yaz("01.09.2026", 40, 120); Yaz("MARKET", 120, 120); Yaz("150,00", 330, 120); Yaz("1.850,00", 490, 120);
        Yaz("02.09.2026", 40, 140); Yaz("MAAS", 120, 140); Yaz("5.000,00", 410, 140); Yaz("6.850,00", 490, 140);
        return builder.Build();
    }

    [Fact]
    public void UretilenPdf_SutunTabanliAyristiricidanDogruOkunur()
    {
        var sonuc = new LayoutStatementParser().Parse(PdfWordExtractor.Extract(OrnekEkstre()));

        Assert.True(sonuc.HeaderFound);
        Assert.Equal([(2, 150m), (1, 5000m)], sonuc.Transactions.Select(t => (t.Type, t.Amount)));
        Assert.Equal("MARKET", sonuc.Transactions[0].Description);
    }
}
