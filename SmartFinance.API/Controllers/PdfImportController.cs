using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFinance.Application.DTOs.PdfImport;
using SmartFinance.Application.Interfaces;

namespace SmartFinance.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PdfImportController : ControllerBase
{
    private readonly IPdfImportService _pdfImportService;

    public PdfImportController(IPdfImportService pdfImportService)
    {
        _pdfImportService = pdfImportService;
    }

    /// <summary>PDF yükle ve parse et (önizleme)</summary>
    [HttpPost("parse")]
    public async Task<IActionResult> ParsePdf(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "Dosya yüklenmedi." });

        var isPdf = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var isExcel = file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        if (!isPdf && !isExcel)
            return BadRequest(new { message = "Sadece PDF veya Excel (.xlsx) dosyaları destekleniyor." });

        if (file.Length > 10 * 1024 * 1024) // 10 MB limit
            return BadRequest(new { message = "Dosya boyutu 10 MB'ı geçemez." });

        using var stream = file.OpenReadStream();
        var result = await _pdfImportService.ParsePdfAsync(stream, file.FileName);

        if (!result.Transactions.Any())
            return Ok(new { message = isPdf
                ? "PDF'den işlem çıkarılamadı. Dosya metin tabanlı olmayabilir."
                : "Excel dosyasından işlem çıkarılamadı. Beklenen ekstre formatında olmayabilir.", result });

        return Ok(result);
    }

    /// <summary>Onaylanan işlemleri toplu kaydet</summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmImport([FromBody] ConfirmImportDto dto)
    {
        if (dto.Transactions == null || !dto.Transactions.Any())
            return BadRequest(new { message = "En az bir işlem seçmelisiniz." });

        var result = await _pdfImportService.ConfirmImportAsync(dto);

        // Ayni ekstre ikinci kez yuklendiginde kullanici "hicbir sey olmadi"
        // sanmasin: ne kaydedildigi ve ne atlandigi acikca soylenir.
        var message = result.SkippedCount == 0
            ? $"{result.SavedCount} işlem başarıyla kaydedildi."
            : result.SavedCount == 0
                ? $"Bu işlemlerin tamamı zaten kayıtlı ({result.SkippedCount} işlem atlandı)."
                : $"{result.SavedCount} işlem kaydedildi, {result.SkippedCount} işlem zaten kayıtlı olduğu için atlandı.";

        return Ok(new { message, count = result.SavedCount, skipped = result.SkippedCount });
    }
}
