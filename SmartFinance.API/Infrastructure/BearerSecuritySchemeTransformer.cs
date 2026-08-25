// Bu sınıf Scalar UI'da "Authorize" butonunun çıkmasını sağlar
// OpenAPI dökümanına JWT Bearer güvenlik kuralı ekler

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;   // Microsoft.OpenApi 2.0'da "Models" alt-namespace'i kaldırıldı

namespace SmartFinance.API.Infrastructure;

// IOpenApiDocumentTransformer = OpenAPI dökümanını değiştiren arayüz
public class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    // TransformAsync = Dönüştür (dökümanı değiştir)
    public Task TransformAsync(
        OpenApiDocument document,                          // Döküman
        OpenApiDocumentTransformerContext context,          // Bağlam
        CancellationToken cancellationToken)                // İptal jetonu
    {
        // 1. JWT Bearer şemasını tanımla
        var securityScheme = new OpenApiSecurityScheme
        {
            Name = "Authorization",                        // Header adı
            In = ParameterLocation.Header,                 // Nerede? Header'da
            Type = SecuritySchemeType.Http,                 // Tip: HTTP
            Scheme = "Bearer",                             // Şema: Bearer
            BearerFormat = "JWT",                          // Format: JWT
            Description = "JWT token'ını buraya yaz. Örnek: eyJhbGci...",
        };

        // 2. Dökümanın güvenlik bileşenlerine ekle
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = securityScheme;

        // 3. Tüm endpoint'lere güvenlik kuralı ekle.
        // OpenApi 2.0'da gereksinim, şemanın kendisiyle değil şemaya bir
        // referansla kuruluyor ("#/components/securitySchemes/Bearer").
        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        };

        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null) continue;

            foreach (var operation in path.Operations.Values)
            {
                operation.Security ??= new List<OpenApiSecurityRequirement>();
                operation.Security.Add(requirement);
            }
        }

        return Task.CompletedTask; // Görev tamamlandı
    }
}
