using SmartFinance.Application.Exceptions;
using System.Net;
using System.Text.Json;

namespace SmartFinance.API.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    // Beklenmeyen hatalarda istemciye donen sabit mesaj. Gercek hata mesaji
    // (baglanti dizesi, SQL detayi, dosya yolu vb. icerebilir) sadece loga yazilir.
    private const string GenericErrorMessage = "Beklenmeyen bir hata oluştu. Lütfen daha sonra tekrar deneyin.";

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var statusCode = exception switch
        {
            BadRequestException => (int)HttpStatusCode.BadRequest,       // 400
            NotFoundException => (int)HttpStatusCode.NotFound,           // 404
            UnauthorizedException => (int)HttpStatusCode.Unauthorized,   // 401
            ExternalServiceException => (int)HttpStatusCode.BadGateway,  // 502
            _ => (int)HttpStatusCode.InternalServerError                 // 500 (bilinmeyen)
        };

        // Bilinen (bizim firlattigimiz) hatalar isin normal akisinin parcasi —
        // uyari seviyesinde, yigin izi olmadan loglanir. Bilinmeyen hatalar
        // gercek bir kusurdur: tam exception yigin iziyle birlikte kaydedilir,
        // yoksa sunucuda ne patladigini anlamanin yolu kalmaz.
        if (statusCode == (int)HttpStatusCode.InternalServerError)
        {
            _logger.LogError(exception,
                "Beklenmeyen hata. {Method} {Path} — TraceId: {TraceId}",
                context.Request.Method, context.Request.Path, context.TraceIdentifier);
        }
        else
        {
            _logger.LogWarning(
                "İşlenen hata ({StatusCode}). {Method} {Path} — {ExceptionType}: {Message}",
                statusCode, context.Request.Method, context.Request.Path,
                exception.GetType().Name, exception.Message);
        }

        context.Response.StatusCode = statusCode;

        var response = new
        {
            statusCode,
            // Bilinen hatalarin mesaji kullaniciya gosterilmek uzere yazildi.
            // Bilinmeyen hatalarda ic detay sizdirmamak icin genel mesaj doner;
            // destek icin traceId ile log kaydina ulasilabilir.
            message = statusCode == (int)HttpStatusCode.InternalServerError
                ? GenericErrorMessage
                : exception.Message,
            traceId = context.TraceIdentifier
        };

        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }
}
