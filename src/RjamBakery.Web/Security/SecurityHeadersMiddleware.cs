namespace RjamBakery.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicyValue =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self'; " +
        "manifest-src 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["X-Permitted-Cross-Domain-Policies"] = "none";
            headers["Content-Security-Policy"] = ContentSecurityPolicyValue;

            if (ShouldDisableCaching(context))
            {
                headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    private static bool ShouldDisableCaching(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/Account"))
        {
            return true;
        }

        // Preserve browser/service-worker caching for static assets and offline POS shell
        if (path.StartsWithSegments("/images")
            || path.StartsWithSegments("/js")
            || path.StartsWithSegments("/_content")
            || path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/sw.js")
            || path.StartsWithSegments("/manifest.json")
            || path.StartsWithSegments("/offline-pos.html")
            || path.Value?.EndsWith(".css", StringComparison.OrdinalIgnoreCase) == true
            || path.Value?.EndsWith(".js", StringComparison.OrdinalIgnoreCase) == true
            || path.Value?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
            || path.Value?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true
            || path.Value?.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        return context.User.Identity?.IsAuthenticated == true;
    }
}
