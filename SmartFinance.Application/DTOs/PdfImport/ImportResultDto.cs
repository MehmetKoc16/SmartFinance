namespace SmartFinance.Application.DTOs.PdfImport;

/// <summary>
/// İçe aktarma sonucu. Yalnızca kaydedilen sayı değil, ATLANAN sayı da
/// dönüyor: kullanıcı aynı ekstreyi ikinci kez yüklediğinde "hiçbir şey
/// olmadı" izlenimi almasın, ne olduğunu görsün.
/// </summary>
public class ImportResultDto
{
    public int SavedCount { get; set; }

    /// Zaten kayıtlı olduğu için eklenmeyen işlem sayısı.
    public int SkippedCount { get; set; }
}
