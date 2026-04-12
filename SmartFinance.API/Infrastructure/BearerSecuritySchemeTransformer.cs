// Bu sınıf Scalar UI'da "Authorize" butonunun çıkmasını sağlar
// OpenAPI dökümanına JWT Bearer güvenlik kuralı ekler

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

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
            Reference = new OpenApiReference
            {
                Id = "Bearer",                             // Referans adı
                Type = ReferenceType.SecurityScheme         // Tip: Güvenlik şeması
            }
        };

        // 2. Dökümanın güvenlik bileşenlerine ekle
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = securityScheme;

        // 3. Tüm endpoint'lere güvenlik kuralı ekle
        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in path.Operations.Values)
            {
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [securityScheme] = Array.Empty<string>()
                });
            }
        }

        return Task.CompletedTask; // Görev tamamlandı
    }
}
