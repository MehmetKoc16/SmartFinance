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

        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Sadece PDF dosyaları destekleniyor." });

        if (file.Length > 10 * 1024 * 1024) // 10 MB limit
            return BadRequest(new { message = "Dosya boyutu 10 MB'ı geçemez." });

        using var stream = file.OpenReadStream();
        var result = await _pdfImportService.ParsePdfAsync(stream, file.FileName);

        if (!result.Transactions.Any())
            return Ok(new { message = "PDF'den işlem çıkarılamadı. Dosya metin tabanlı olmayabilir.", result });

        return Ok(result);
    }

    /// <summary>Onaylanan işlemleri toplu kaydet</summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmImport([FromBody] ConfirmImportDto dto)
    {
        if (dto.Transactions == null || !dto.Transactions.Any())
            return BadRequest(new { message = "En az bir işlem seçmelisiniz." });

        var savedCount = await _pdfImportService.ConfirmImportAsync(dto);
        return Ok(new { message = $"{savedCount} işlem başarıyla kaydedildi.", count = savedCount });
    }
}
