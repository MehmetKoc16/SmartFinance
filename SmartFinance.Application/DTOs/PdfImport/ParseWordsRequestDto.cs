using System.ComponentModel.DataAnnotations;

namespace SmartFinance.Application.DTOs.PdfImport;

/// <summary>
/// Taranmis ekstrenin telefondaki OCR ciktisi. Sayfa goruntusu telefondan
/// cikmaz; yalnizca okunan kelimeler ve sayfadaki konumlari gelir.
/// </summary>
public class ParseWordsRequestDto
{
    // 50 sayfalik yogun bir ekstre ~15.000 kelime; sinir bunun iki kati.
    public const int MaxWords = 30000;

    [Required(ErrorMessage = "Kelime listesi boş olamaz!")]
    [MinLength(1, ErrorMessage = "Kelime listesi boş olamaz!")]
    [MaxLength(MaxWords, ErrorMessage = "Ekstre çok büyük, en fazla 50 sayfa yükleyebilirsiniz.")]
    public List<OcrWordDto> Words { get; set; } = new();
}

/// <summary>Koordinatlar sayfa goruntusunun sol ust kosesinden, piksel.</summary>
public class OcrWordDto
{
    [Required(ErrorMessage = "Kelime metni boş olamaz!")]
    [StringLength(200, ErrorMessage = "Kelime en fazla 200 karakter olabilir!")]
    public string Text { get; set; } = string.Empty;

    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }

    [Range(1, 50, ErrorMessage = "Sayfa numarası 1 ile 50 arasında olmalı!")]
    public int Page { get; set; } = 1;
}
