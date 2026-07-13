using SmartFinance.Application.Exceptions;
using System.Net;
using System.Text.Json;

namespace SmartFinance.API.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public ExceptionMiddleware(RequestDelegate next)
    {
        _next=next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try{
            await _next(context);
        }
        catch(Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    // Bu metot sınıfın İÇİNDE olmalı!
    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType="application/json";

        var statusCode = exception switch
        {
            BadRequestException => (int)HttpStatusCode.BadRequest,       // 400
            NotFoundException =>(int)HttpStatusCode.NotFound,            // 404
            UnauthorizedException=>(int)HttpStatusCode.Unauthorized,     // 401
            ExternalServiceException=>(int)HttpStatusCode.BadGateway,    // 502
            _ => (int)HttpStatusCode.InternalServerError                 // 500 (bilinmeyen)
        };

        context.Response.StatusCode = statusCode;

        var response = new{
            statusCode=statusCode,
            message=exception.Message
        };
        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }
}