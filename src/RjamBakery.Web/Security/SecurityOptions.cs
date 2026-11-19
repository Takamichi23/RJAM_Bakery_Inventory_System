namespace RjamBakery.Web.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public const string AuthRateLimitPolicy = "rjam-auth";
    public const string ApiRateLimitPolicy = "rjam-api";
    public const string ExportRateLimitPolicy = "rjam-export";
    public const string ConfiguredCorsPolicy = "rjam-cors";

    /// <summary>
    /// Redirect HTTP to HTTPS in Production. Set to false when hosting purely over a local Wi-Fi LAN without TLS.
    /// </summary>
    public bool EnableHttpsRedirection { get; set; } = true;

    /// <summary>
    /// Force Secure flag on Auth & Antiforgery cookies regardless of request scheme.
    /// When false, cookies use SameAsRequest (automatically Secure on HTTPS, functional on HTTP LAN).
    /// </summary>
    public bool RequireHttpsCookies { get; set; }

    /// <summary>
    /// Trust X-Forwarded-For and X-Forwarded-Proto headers from reverse proxies (Nginx, Caddy, Cloudflare, Railway, etc.).
    /// </summary>
    public bool TrustForwardedHeaders { get; set; } = true;

    /// <summary>
    /// Optional explicit cross-origin allowlist (e.g. "https://pos.rjambakery.com"). Empty = strict same-origin only.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Enable IP/user rate limiting across Auth, API, and Report Export endpoints.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;

    public int AuthPermitLimitPerMinute { get; set; } = 10;
    public int ApiPermitLimitPerMinute { get; set; } = 120;
    public int ExportPermitLimitPerMinute { get; set; } = 20;

    /// <summary>
    /// Maximum HTTP request body size (default 5 MB) to mitigate oversized payload DoS attacks.
    /// </summary>
    public long MaxRequestBodySizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Optional directory path for persisting ASP.NET Core DataProtection keys across restarts/deployments.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }
}
