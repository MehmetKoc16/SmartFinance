using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SmartFinance.API.Infrastructure;

/// Model dogrulama (DTO uzerindeki [Required]/[Range] vb.) hatalarinin
/// istemciye hangi bicimde donecegini belirler.
public static class ValidationProblemResponseFactory
{
    /// [ApiController] varsayilan olarak RFC 7807 ProblemDetails doner:
    /// {"title":"One or more validation errors occurred.","errors":{...}}.
    /// Istemci ise diger tum hatalarda oldugu gibi "message" alanina bakiyor —
    /// bu yuzden DTO'lardaki Turkce ErrorMessage metinleri kullaniciya hic
    /// ulasmiyor, yerine genel "İşlem başarısız" gosteriliyordu.
    ///
    /// Yanit, ExceptionMiddleware'in urettigi bicimle ayni hale getirilir:
    /// { statusCode, message, traceId }.
    public static IActionResult Create(ActionContext context)
    {
        var messages = context.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToArray();

        return new BadRequestObjectResult(new
        {
            statusCode = StatusCodes.Status400BadRequest,
            // Birden fazla alan hataliysa hepsi gosterilir; kullanici formu
            // tek seferde duzeltebilsin diye tek tek degil.
            message = messages.Length > 0
                ? string.Join("\n", messages)
                : "Gönderilen veri geçersiz.",
            traceId = context.HttpContext.TraceIdentifier
        });
    }
}
