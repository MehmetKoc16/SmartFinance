namespace SmartFinance.Application.DTOs.PdfImport;

/// <summary>
/// Sayfadaki konumuyla bir kelime. Koordinatlar sol UST koseden, asagi dogru
/// artan (Top &lt; Bottom). Kaynak PDF'in gomulu metni de olabilir, telefondaki
/// OCR ciktisi da; ayristirici ikisini ayni sekilde isler.
/// </summary>
public record PositionedWord(string Text, double Left, double Top, double Right, double Bottom, int Page = 1)
{
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
    public double Height => Bottom - Top;
}
