using SmartFinance.Application.DTOs.PdfImport;
using UglyToad.PdfPig;

namespace SmartFinance.Infrastructure.BankParsers;

/// <summary>
/// PDF'in gomulu metnini konumlu kelimelere cevirir. PdfPig koordinatlari sol
/// ALT koseden ve yukari dogru; LayoutStatementParser (ve OCR ciktisi) sol ust
/// koseden asagi dogru kullaniyor, bu yuzden dikey eksen sayfa yuksekliginden
/// cikarilarak cevriliyor.
/// </summary>
public static class PdfWordExtractor
{
    public static List<PositionedWord> Extract(byte[] pdf)
    {
        var sonuc = new List<PositionedWord>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            foreach (var w in page.GetWords())
            {
                var b = w.BoundingBox;
                sonuc.Add(new PositionedWord(w.Text, b.Left, page.Height - b.Top, b.Right, page.Height - b.Bottom, page.Number));
            }
        }
        return sonuc;
    }
}
