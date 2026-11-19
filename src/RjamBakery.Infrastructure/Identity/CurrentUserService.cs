using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RjamBakery.Application.Abstractions;
using RjamBakery.Domain.Common;

namespace RjamBakery.Infrastructure.Identity;

/// <summary>
/// Holds the active ClaimsPrincipal for the current Blazor circuit or HTTP request scope.
/// </summary>
public sealed class ScopedUserAccessor
{
    public ClaimsPrincipal? Principal { get; set; }
}

public sealed class CurrentUserService(
    IHttpContextAccessor httpContextAccessor,
    ScopedUserAccessor scopedUserAccessor) : ICurrentUserService
{
    private ClaimsPrincipal? ActivePrincipal
    {
        get
        {
            if (scopedUserAccessor.Principal?.Identity?.IsAuthenticated == true)
            {
                return scopedUserAccessor.Principal;
            }

            var httpUser = httpContextAccessor.HttpContext?.User;
            return httpUser?.Identity?.IsAuthenticated == true ? httpUser : null;
        }
    }

    public string? UserId => ActivePrincipal?.FindFirstValue(ClaimTypes.NameIdentifier);
    public string UserName => ActivePrincipal?.Identity?.Name ?? string.Empty;
    public string FullName => ActivePrincipal?.FindFirstValue("FullName") ?? UserName;
    public string PrimaryRole => ActivePrincipal?.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool IsAuthenticated => ActivePrincipal?.Identity?.IsAuthenticated == true;

    public bool IsInRole(string role) => ActivePrincipal?.IsInRole(role) == true;

    public string EnsureAuthenticatedUserId()
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(UserId))
        {
            throw new UnauthorizedDomainActionException("Authentication is required to perform this operation.");
        }

        return UserId;
    }

    public void EnsureInRole(params string[] allowedRoles)
    {
        EnsureAuthenticatedUserId();
        if (allowedRoles.Length == 0)
        {
            return;
        }

        if (!allowedRoles.Any(IsInRole))
        {
            throw new UnauthorizedDomainActionException(
                $"Access denied. Required role: {string.Join(" or ", allowedRoles)}.");
        }
    }
}

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime LocalNow => DateTime.Now;
    public DateOnly TodayLocal => DateOnly.FromDateTime(DateTime.Now);
}
