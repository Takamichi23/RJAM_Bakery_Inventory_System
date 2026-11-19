using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Security;

namespace RjamBakery.Web.Endpoints;

public sealed record LoginApiRequest(string UserName, string Password, bool RememberMe = false);
public sealed record ChangePasswordApiRequest(string CurrentPassword, string NewPassword);
public sealed record CategoryStatusRequest(bool IsActive);
public sealed record ArchiveToggleRequest(bool Archive);
public sealed record UserActiveToggleRequest(bool IsActive);

public static class Phase2ApiEndpoints
{
    public static IEndpointRouteBuilder MapPhase2ApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireRateLimiting(SecurityOptions.ApiRateLimitPolicy);

        // Auth endpoints
        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", async (
            [FromBody] LoginApiRequest request,
            HttpContext http,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IAuditLogService audit,
            RjamBakery.Application.Abstractions.IAppDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("RjamBakery.Auth");
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { message = "Username and password are required." });
            }

            var trimmedUser = request.UserName.Trim();
            var user = await userManager.FindByNameAsync(trimmedUser);
            if (user is null)
            {
                logger.LogWarning("Failed API login for unknown username '{UserName}' from {RemoteIp}.",
                    trimmedUser, http.Connection.RemoteIpAddress?.ToString());
                return Results.Unauthorized();
            }

            if (!user.IsActive)
            {
                logger.LogWarning("Blocked API login for deactivated user '{UserName}' from {RemoteIp}.",
                    user.UserName, http.Connection.RemoteIpAddress?.ToString());
                await audit.AddAuditEntryAsync(
                    "UserLoginBlocked",
                    nameof(ApplicationUser),
                    user.Id,
                    $"Deactivated account '{user.UserName}' attempted API login.",
                    explicitUserId: user.Id,
                    explicitUserName: user.UserName ?? string.Empty,
                    explicitUserRole: "Inactive",
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.Unauthorized();
            }

            var result = await signInManager.PasswordSignInAsync(user, request.Password, request.RememberMe, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                var rolesOnFail = await userManager.GetRolesAsync(user);
                var failAction = result.IsLockedOut ? "UserLoginLockedOut" : "UserLoginFailed";
                logger.LogWarning("{Action} via API for user '{UserName}' from {RemoteIp}.",
                    failAction, user.UserName, http.Connection.RemoteIpAddress?.ToString());
                await audit.AddAuditEntryAsync(
                    failAction,
                    nameof(ApplicationUser),
                    user.Id,
                    result.IsLockedOut
                        ? $"Account '{user.UserName}' locked out after repeated failed API logins."
                        : $"Invalid API login attempt for '{user.UserName}'.",
                    explicitUserId: user.Id,
                    explicitUserName: user.UserName ?? string.Empty,
                    explicitUserRole: rolesOnFail.FirstOrDefault() ?? "User",
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.Unauthorized();
            }

            user.LastLoginAt = DateTime.UtcNow;
            await userManager.UpdateAsync(user);

            var roles = await userManager.GetRolesAsync(user);
            await audit.AddAuditEntryAsync(
                "UserLogin",
                nameof(ApplicationUser),
                user.Id,
                $"User '{user.UserName}' signed in.",
                explicitUserId: user.Id,
                explicitUserName: user.UserName ?? string.Empty,
                explicitUserRole: roles.FirstOrDefault() ?? "User",
                ct: ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                user.Id,
                user.UserName,
                user.FullName,
                Role = roles.FirstOrDefault() ?? string.Empty,
                user.MustChangePassword
            });
        }).AllowAnonymous().RequireRateLimiting(SecurityOptions.AuthRateLimitPolicy);

        auth.MapPost("/change-password", async (
            [FromBody] ChangePasswordApiRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RjamBakery.Application.Abstractions.ICurrentUserService currentUser,
            IAuditLogService audit,
            RjamBakery.Application.Abstractions.IAppDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                throw new DomainValidationException("Current password and new password are required.");
            }

            if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            {
                throw new DomainValidationException("New password must be different from your current password.");
            }

            var userId = currentUser.EnsureAuthenticatedUserId();
            var user = await userManager.FindByIdAsync(userId)
                ?? throw new EntityNotFoundException(nameof(ApplicationUser), userId);

            var changeResult = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!changeResult.Succeeded)
            {
                throw new DomainValidationException(string.Join("; ", changeResult.Errors.Select(e => e.Description)));
            }

            user.MustChangePassword = false;
            await userManager.UpdateAsync(user);
            await userManager.UpdateSecurityStampAsync(user);
            await signInManager.RefreshSignInAsync(user);

            await audit.AddAuditEntryAsync(
                "UserPasswordChanged",
                nameof(ApplicationUser),
                user.Id,
                $"User '{user.UserName}' updated their password via API.",
                ct: ct);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { message = "Password updated successfully." });
        }).RequireAuthorization().RequireRateLimiting(SecurityOptions.AuthRateLimitPolicy);

        auth.MapPost("/logout", async (
            SignInManager<ApplicationUser> signInManager,
            RjamBakery.Application.Abstractions.ICurrentUserService currentUser,
            IAuditLogService audit,
            RjamBakery.Application.Abstractions.IAppDbContext db,
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
            return Results.Ok(new { message = "Signed out." });
        }).RequireAuthorization();

        // Categories
        var categories = api.MapGroup("/categories").RequireAuthorization();
        categories.MapGet("/", async (bool? includeInactive, ICategoryService service, CancellationToken ct)
            => Results.Ok(await service.GetAllAsync(includeInactive ?? false, ct)))
            .RequireAuthorization(AppPolicies.ViewCatalog);

        categories.MapPost("/", async ([FromBody] SaveCategoryRequest request, ICategoryService service, CancellationToken ct)
            => Results.Ok(await service.SaveAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageCatalog);

        categories.MapPatch("/{id:int}/status", async (int id, [FromBody] CategoryStatusRequest request, ICategoryService service, CancellationToken ct) =>
        {
            await service.SetActiveStatusAsync(id, request.IsActive, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ManageCatalog);

        // Units
        api.MapGet("/units", async (IProductService service, CancellationToken ct)
            => Results.Ok(await service.GetUnitsOfMeasureAsync(ct)))
            .RequireAuthorization(AppPolicies.ViewCatalog);

        // Products
        var products = api.MapGroup("/products").RequireAuthorization();
        products.MapGet("/", async (
            string? search,
            int? categoryId,
            bool? includeArchived,
            int? page,
            int? pageSize,
            IProductService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new ProductQuery(search, categoryId, includeArchived ?? false, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ViewCatalog);

        products.MapGet("/{id:int}", async (int id, IProductService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ViewCatalog);

        products.MapPost("/", async ([FromBody] SaveProductRequest request, IProductService service, CancellationToken ct)
            => Results.Ok(await service.SaveAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageCatalog);

        products.MapPatch("/{id:int}/archive", async (int id, [FromBody] ArchiveToggleRequest request, IProductService service, CancellationToken ct) =>
        {
            await service.SetArchiveStatusAsync(id, request.Archive, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ManageCatalog);

        // Raw Materials
        var rawMaterials = api.MapGroup("/raw-materials").RequireAuthorization();
        rawMaterials.MapGet("/", async (
            string? search,
            bool? lowStockOnly,
            bool? outOfStockOnly,
            bool? includeArchived,
            int? page,
            int? pageSize,
            IRawMaterialService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new RawMaterialQuery(search, lowStockOnly ?? false, outOfStockOnly ?? false, includeArchived ?? false, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ViewRawMaterials);

        rawMaterials.MapGet("/{id:int}", async (int id, IRawMaterialService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ViewRawMaterials);

        rawMaterials.MapPost("/", async ([FromBody] SaveRawMaterialRequest request, IRawMaterialService service, CancellationToken ct)
            => Results.Ok(await service.SaveAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageRawMaterials);

        rawMaterials.MapPatch("/{id:int}/archive", async (int id, [FromBody] ArchiveToggleRequest request, IRawMaterialService service, CancellationToken ct) =>
        {
            await service.SetArchiveStatusAsync(id, request.Archive, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ManageRawMaterials);

        rawMaterials.MapPost("/{id:int}/adjust", async (int id, [FromBody] RawMaterialAdjustmentRequest request, IRawMaterialService service, CancellationToken ct) =>
        {
            var adj = request with { RawMaterialId = id };
            return Results.Ok(await service.AdjustStockAsync(adj, ct));
        }).RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        // Inventory Ledger
        var ledger = api.MapGroup("/ledger").RequireAuthorization();
        ledger.MapGet("/movements", async (
            InventoryItemType? itemType,
            int? rawMaterialId,
            int? productId,
            InventoryMovementType? movementType,
            string? referenceNumber,
            int? page,
            int? pageSize,
            IInventoryLedgerService service,
            CancellationToken ct)
            => Results.Ok(await service.QueryMovementsAsync(
                new InventoryMovementQuery(itemType, rawMaterialId, productId, movementType, null, null, referenceNumber, page ?? 1, pageSize ?? 50), ct)));

        ledger.MapGet("/verify", async (IInventoryLedgerService service, CancellationToken ct)
            => Results.Ok(await service.VerifyBalanceIntegrityAsync(ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        // Audit Logs
        api.MapGet("/audit-logs", async (
            string? userId,
            string? action,
            string? entityName,
            int? page,
            int? pageSize,
            IAuditLogService service,
            CancellationToken ct)
            => Results.Ok(await service.QueryAsync(
                new AuditLogQuery(userId, action, entityName, null, null, page ?? 1, pageSize ?? 50), ct)))
            .RequireAuthorization(AppPolicies.ViewAuditLogs);

        // System Settings
        var settings = api.MapGroup("/settings").RequireAuthorization();
        settings.MapGet("/operational", async (ISystemSettingService service, CancellationToken ct)
            => Results.Ok(await service.GetOperationalConfigAsync(ct)));

        settings.MapGet("/", async (ISystemSettingService service, CancellationToken ct)
            => Results.Ok(await service.GetAllAsync(ct)))
            .RequireAuthorization(AppPolicies.ViewAuditLogs);

        settings.MapPut("/", async ([FromBody] UpdateSystemSettingRequest request, ISystemSettingService service, CancellationToken ct)
            => Results.Ok(await service.UpdateAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageSettings);

        // User Management
        var users = api.MapGroup("/users").RequireAuthorization(AppPolicies.ManageUsers);
        users.MapGet("/", async (string? search, int? page, int? pageSize, IUserManagementService service, CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(search, page ?? 1, pageSize ?? 25, ct)));

        users.MapGet("/{id}", async (string id, IUserManagementService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)));

        users.MapPost("/", async ([FromBody] CreateUserRequest request, IUserManagementService service, CancellationToken ct)
            => Results.Ok(await service.CreateAsync(request, ct)));

        users.MapPut("/{id}", async (string id, [FromBody] UpdateUserRequest request, IUserManagementService service, CancellationToken ct)
            => Results.Ok(await service.UpdateAsync(request with { Id = id }, ct)));

        users.MapPost("/{id}/reset-password", async (string id, [FromBody] ResetPasswordRequest request, IUserManagementService service, CancellationToken ct) =>
        {
            await service.ResetPasswordAsync(request with { UserId = id }, ct);
            return Results.NoContent();
        });

        users.MapPatch("/{id}/active", async (string id, [FromBody] UserActiveToggleRequest request, IUserManagementService service, CancellationToken ct) =>
        {
            await service.SetActiveStatusAsync(id, request.IsActive, ct);
            return Results.NoContent();
        });

        return app;
    }
}
