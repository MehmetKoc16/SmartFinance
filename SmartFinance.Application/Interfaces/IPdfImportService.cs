using SmartFinance.Application.DTOs.PdfImport;

namespace SmartFinance.Application.Interfaces;

public interface IPdfImportService
{
    Task<PdfParseResultDto> ParsePdfAsync(Stream pdfStream, string fileName);
    Task<ImportResultDto> ConfirmImportAsync(ConfirmImportDto dto);
}
