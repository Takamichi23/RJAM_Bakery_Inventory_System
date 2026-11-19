using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace RjamBakery.Web.Security;

/// <summary>
/// Defends cookie-authenticated /api/* mutation endpoints against Cross-Site Request Forgery (CSRF)
/// by enforcing Same-Origin / Allowlisted Origin checks, Sec-Fetch-Site validation, and blocking
/// HTML form-encoded content types on JSON API routes.
/// </summary>
public sealed class ApiOriginValidationMiddleware(
    RequestDelegate next,
    IOptions<SecurityOptions> securityOptions,
    ILogger<ApiOriginValidationMiddleware> logger)
{
    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")
            || !UnsafeMethods.Contains(context.Request.Method))
        {
            await next(context);
            return;
        }

        var options = securityOptions.Value;

        // 1. Block classic HTML form CSRF MIME types on JSON API endpoints
        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType)
            && (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("Rejected API mutation {Method} {Path} with disallowed Content-Type '{ContentType}'.",
                context.Request.Method, context.Request.Path, contentType);
            await WriteProblemAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "API requests must use application/json.");
            return;
        }

        var allowedOrigins = new HashSet<string>(
            (options.AllowedOrigins ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).Select(NormalizeOrigin),
            StringComparer.OrdinalIgnoreCase);

        var originHeader = context.Request.Headers.Origin.FirstOrDefault();
        var refererHeader = context.Request.Headers.Referer.FirstOrDefault();
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();

        // 2. Reject explicit cross-site browser requests unless explicitly allowlisted
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(originHeader) || !allowedOrigins.Contains(NormalizeOrigin(originHeader)))
            {
                logger.LogWarning("Blocked cross-site API mutation {Method} {Path} (Sec-Fetch-Site: cross-site, Origin: {Origin}).",
                    context.Request.Method, context.Request.Path, originHeader ?? "<none>");
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "CSRF_BLOCKED",
                    "Cross-site API requests are not permitted.");
                return;
            }
        }

        // 3. Validate Origin header when supplied by browsers
        if (!string.IsNullOrWhiteSpace(originHeader))
        {
            if (string.Equals(originHeader, "null", StringComparison.OrdinalIgnoreCase)
                || !IsSameOriginOrAllowed(context.Request, originHeader, allowedOrigins))
            {
                logger.LogWarning("Blocked API mutation {Method} {Path} due to untrusted Origin '{Origin}'.",
                    context.Request.Method, context.Request.Path, originHeader);
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "CSRF_ORIGIN_MISMATCH",
                    "Request origin is not authorized for this API.");
                return;
            }
        }
        else if (!string.IsNullOrWhiteSpace(refererHeader))
        {
            // Fallback check on Referer when Origin is omitted
            if (!IsSameOriginOrAllowed(context.Request, refererHeader, allowedOrigins))
            {
                logger.LogWarning("Blocked API mutation {Method} {Path} due to untrusted Referer '{Referer}'.",
                    context.Request.Method, context.Request.Path, refererHeader);
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "CSRF_ORIGIN_MISMATCH",
                    "Request referer is not authorized for this API.");
                return;
            }
        }

        await next(context);
    }

    private static bool IsSameOriginOrAllowed(HttpRequest request, string candidateUrl, HashSet<string> allowedOrigins)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidateUri))
        {
            return false;
        }

        var normalizedCandidate = NormalizeOrigin(candidateUri.GetLeftPart(UriPartial.Authority));
        if (allowedOrigins.Contains(normalizedCandidate))
        {
            return true;
        }

        var requestHost = request.Host;
        if (!requestHost.HasValue)
        {
            return false;
        }

        var expectedPort = requestHost.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(candidateUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
            && candidateUri.Port == expectedPort
            && string.Equals(candidateUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOrigin(string origin)
        => origin.Trim().TrimEnd('/');

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = code,
            Detail = detail,
            Status = status,
            Instance = context.Request.Path
        });
    }
}
