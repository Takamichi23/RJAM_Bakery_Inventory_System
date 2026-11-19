using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Services;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Entities;
using RjamBakery.Infrastructure;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Web.Auth;
using RjamBakery.Web.Components;
using RjamBakery.Web.Endpoints;
using RjamBakery.Web.Middleware;
using RjamBakery.Web.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var securityOptions = builder.Services.AddRjamSecurity(builder.Configuration);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    if (securityOptions.MaxRequestBodySizeBytes > 0)
    {
        kestrel.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodySizeBytes;
    }
});

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddRjamInfrastructure(builder.Configuration);
builder.Services.AddRjamAuthorization(securityOptions.RequireHttpsCookies);
builder.Services.AddValidatorsFromAssemblyContaining<SaveProductRequestValidator>();

builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedReferenceDefaultsAsync();
}

if (securityOptions.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (securityOptions.EnableHttpsRedirection)
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.Use(async (ctx, next) =>
{
    if ((ctx.Request.Path.StartsWithSegments("/api")
        || ctx.Request.Path.StartsWithSegments("/images")
        || ctx.Request.Path.StartsWithSegments("/js")
        || ctx.Request.Path.StartsWithSegments("/sw.js")
        || ctx.Request.Path.StartsWithSegments("/manifest.json")
        || ctx.Request.Path.StartsWithSegments("/offline-pos.html")
        || ctx.Request.Path.StartsWithSegments("/_framework")
        || ctx.Request.Path.StartsWithSegments("/_content"))
        && ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } statusFeature)
    {
        statusFeature.Enabled = false;
    }

    await next();
});
app.UseCors(SecurityOptions.ConfiguredCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<ApiOriginValidationMiddleware>();
app.UseMiddleware<DomainExceptionMiddleware>();
app.UseAntiforgery();

app.MapPost("/Account/Logout", async (
    SignInManager<ApplicationUser> signInManager,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IAppDbContext db,
    CancellationToken ct) =>
{
    if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.UserId))
    {
        await audit.AddAuditEntryAsync(
            "UserLogout",
            nameof(ApplicationUser),
            currentUser.UserId,
            $"User '{currentUser.UserName}' signed out.",
            ct: ct);
        await db.SaveChangesAsync(ct);
    }

    await signInManager.SignOutAsync();
    return Results.Redirect("/Account/Login");
}).RequireAuthorization();

app.MapPhase2ApiEndpoints();
app.MapPhase3ApiEndpoints();
app.MapPhase4ApiEndpoints();
app.MapPhase5ApiEndpoints();
app.MapPhase6ApiEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
