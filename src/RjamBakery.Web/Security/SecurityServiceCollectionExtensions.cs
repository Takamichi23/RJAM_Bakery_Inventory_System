using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace RjamBakery.Web.Security;

public static class SecurityServiceCollectionExtensions
{
    public static SecurityOptions AddRjamSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SecurityOptions.SectionName);
        services.Configure<SecurityOptions>(section);

        var options = section.Get<SecurityOptions>() ?? new SecurityOptions();

        // Ensure malformed Minimal API JSON/binding errors flow to DomainExceptionMiddleware for sanitized ProblemDetails
        services.Configure<RouteHandlerOptions>(routeOptions =>
        {
            routeOptions.ThrowOnBadRequest = true;
        });

        if (options.TrustForwardedHeaders)
        {
            services.Configure<ForwardedHeadersOptions>(fh =>
            {
                fh.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                fh.KnownNetworks.Clear();
                fh.KnownProxies.Clear();
                fh.ForwardLimit = 2;
            });
        }

        services.AddAntiforgery(af =>
        {
            af.Cookie.Name = "Rjam.Antiforgery";
            af.Cookie.HttpOnly = true;
            af.Cookie.SameSite = SameSiteMode.Lax;
            af.Cookie.SecurePolicy = options.RequireHttpsCookies
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            af.HeaderName = "X-CSRF-TOKEN";
        });

        var allowedOrigins = (options.AllowedOrigins ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        services.AddCors(cors =>
        {
            cors.AddPolicy(SecurityOptions.ConfiguredCorsPolicy, policy =>
            {
                if (allowedOrigins.Length > 0)
                {
                    policy.WithOrigins(allowedOrigins)
                        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                        .WithHeaders("Content-Type", "Accept", "X-Requested-With", "X-CSRF-TOKEN")
                        .AllowCredentials();
                }
                else
                {
                    // Strict same-origin default: no cross-origin access allowed
                    policy.SetIsOriginAllowed(_ => false);
                }
            });
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (!options.EnableRateLimiting)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                if (HttpMethods.IsPost(httpContext.Request.Method)
                    && (httpContext.Request.Path.StartsWithSegments("/Account/Login")
                        || httpContext.Request.Path.StartsWithSegments("/Account/Setup")
                        || httpContext.Request.Path.StartsWithSegments("/Account/ChangePassword")))
                {
                    return RateLimitPartition.GetFixedWindowLimiter($"web-auth:{GetClientIp(httpContext)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, options.AuthPermitLimitPerMinute),
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
                }

                return RateLimitPartition.GetNoLimiter("default");
            });

            limiter.OnRejected = async (ctx, ct) =>
            {
                int retryAfterSeconds = 60;
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                }

                ctx.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.HttpContext.Response.ContentType = "application/problem+json";

                await ctx.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "RATE_LIMIT_EXCEEDED",
                    Detail = $"Too many requests. Please wait {retryAfterSeconds} seconds and try again.",
                    Status = StatusCodes.Status429TooManyRequests,
                    Instance = ctx.HttpContext.Request.Path
                }, ct);
            };

            limiter.AddPolicy(SecurityOptions.AuthRateLimitPolicy, httpContext =>
            {
                if (!options.EnableRateLimiting)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var key = $"auth:{GetClientIp(httpContext)}";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.AuthPermitLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            limiter.AddPolicy(SecurityOptions.ApiRateLimitPolicy, httpContext =>
            {
                if (!options.EnableRateLimiting)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var key = string.IsNullOrWhiteSpace(userId)
                    ? $"api-ip:{GetClientIp(httpContext)}"
                    : $"api-user:{userId}";

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(10, options.ApiPermitLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            limiter.AddPolicy(SecurityOptions.ExportRateLimitPolicy, httpContext =>
            {
                if (!options.EnableRateLimiting)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? GetClientIp(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter($"export:{userId}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.ExportPermitLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        var dp = services.AddDataProtection().SetApplicationName("RjamBakery");
        if (!string.IsNullOrWhiteSpace(options.DataProtectionKeysPath))
        {
            var dir = new DirectoryInfo(options.DataProtectionKeysPath);
            dir.Create();
            dp.PersistKeysToFileSystem(dir);
        }

        return options;
    }

    private static string GetClientIp(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
