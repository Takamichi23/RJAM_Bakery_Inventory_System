using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Web.Auth;

public sealed class RjamClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim("FullName", user.FullName));
        return identity;
    }
}

public static class AuthorizationExtensions
{
    public static IServiceCollection AddRjamAuthorization(
        this IServiceCollection services,
        bool requireHttpsCookies = false)
    {
        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, RjamClaimsPrincipalFactory>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "Rjam.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttpsCookies
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.Cookie.IsEssential = true;
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        context.RejectPrincipal();
                        return;
                    }

                    var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await userManager.FindByIdAsync(userId);
                    if (user is null || !user.IsActive)
                    {
                        context.RejectPrincipal();
                        return;
                    }

                    await SecurityStampValidator.ValidatePrincipalAsync(context);
                },
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(AppPolicies.OwnerOnly, p => p.RequireRole(AppRoles.Owner))
            .AddPolicy(AppPolicies.ManagementOrOwner, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.ManageCatalog, p => p.RequireRole(AppRoles.Owner))
            .AddPolicy(AppPolicies.ViewCatalog, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier))
            .AddPolicy(AppPolicies.ManageSuppliers, p => p.RequireRole(AppRoles.Owner))
            .AddPolicy(AppPolicies.ViewSuppliers, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker))
            .AddPolicy(AppPolicies.ManageRawMaterials, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.ViewRawMaterials, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker))
            .AddPolicy(AppPolicies.ReceiveRawMaterials, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker))
            .AddPolicy(AppPolicies.ManageRecipes, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.OperateProduction, p => p.RequireRole(AppRoles.Owner, AppRoles.Baker))
            .AddPolicy(AppPolicies.ViewFactoryInventory, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker))
            .AddPolicy(AppPolicies.DispatchDeliveries, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker))
            .AddPolicy(AppPolicies.ReceiveDeliveries, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier))
            .AddPolicy(AppPolicies.ViewStoreInventory, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier))
            .AddPolicy(AppPolicies.OperatePos, p => p.RequireRole(AppRoles.Owner, AppRoles.Cashier))
            .AddPolicy(AppPolicies.RecordWasteOrReturn, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier))
            .AddPolicy(AppPolicies.ApproveAdjustmentsAndVariances, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.SubmitDailyClosing, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier))
            .AddPolicy(AppPolicies.ViewAuditLogs, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.ViewReports, p => p.RequireRole(AppRoles.Owner, AppRoles.Manager))
            .AddPolicy(AppPolicies.ManageUsers, p => p.RequireRole(AppRoles.Owner))
            .AddPolicy(AppPolicies.ManageSettings, p => p.RequireRole(AppRoles.Owner));

        return services;
    }
}
