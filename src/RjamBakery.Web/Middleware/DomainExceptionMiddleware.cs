using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RjamBakery.Domain.Common;

namespace RjamBakery.Web.Middleware;

public sealed class DomainExceptionMiddleware(
    RequestDelegate next,
    ILogger<DomainExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            var status = ex switch
            {
                UnauthorizedDomainActionException => StatusCodes.Status403Forbidden,
                EntityNotFoundException => StatusCodes.Status404NotFound,
                DuplicateTransactionException => StatusCodes.Status409Conflict,
                ConcurrencyConflictException => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };

            logger.LogWarning("Domain rule rejected request {Path} ({Code}): {Message}", context.Request.Path, ex.Code, ex.Message);

            await WriteProblemAsync(context, status, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is BadHttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Malformed request rejected at {Path}", context.Request.Path);
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "INVALID_REQUEST",
                "The request payload is malformed or exceeds size limits.");
        }
        catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api"))
        {
            var traceId = context.TraceIdentifier;
            logger.LogError(ex, "Unhandled API error at {Path} (TraceId: {TraceId})", context.Request.Path, traceId);
            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "INTERNAL_SERVER_ERROR",
                $"An unexpected server error occurred. Reference ID: {traceId}");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = code,
            Detail = detail,
            Status = status,
            Instance = context.Request.Path
        });
    }
}
