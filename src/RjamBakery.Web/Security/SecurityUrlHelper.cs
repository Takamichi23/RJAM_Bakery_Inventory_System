namespace RjamBakery.Web.Security;

public static class SecurityUrlHelper
{
    /// <summary>
    /// Validates that a ReturnUrl is strictly a safe local relative path and cannot be abused for open redirects
    /// (blocks "//evil.com", "/\evil.com", absolute URLs, javascript: URIs, and control characters).
    /// </summary>
    public static string GetSafeLocalReturnUrl(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        var trimmed = returnUrl.Trim();

        // Reject control characters (CR/LF header splitting or tab tricks)
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return fallback;
            }
        }

        // Must start with a single '/' and NOT '//' or '/\'
        if (trimmed[0] != '/')
        {
            return fallback;
        }

        if (trimmed.Length > 1 && (trimmed[1] == '/' || trimmed[1] == '\\'))
        {
            return fallback;
        }

        // Reject backslashes anywhere (browsers normalize '\' to '/' in URLs)
        if (trimmed.Contains('\\'))
        {
            return fallback;
        }

        // Reject encoded double-slash tricks at start
        if (trimmed.StartsWith("/%2f", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/%5c", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        // Ensure .NET treats it strictly as a relative URI and not an absolute URI
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return fallback;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Relative, out _))
        {
            return fallback;
        }

        // Prevent redirecting back to login/logout loops
        if (trimmed.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return trimmed;
    }
}
