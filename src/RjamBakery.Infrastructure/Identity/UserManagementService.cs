using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Infrastructure.Persistence;

namespace RjamBakery.Infrastructure.Identity;

public sealed class UserManagementService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<CreateUserRequest> createValidator,
    IValidator<UpdateUserRequest> updateValidator,
    IValidator<ResetPasswordRequest> resetValidator) : IUserManagementService
{
    public async Task<PagedResult<UserDto>> GetPagedAsync(
        string? searchTerm = null,
        int pageNumber = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 200);

        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            q = q.Where(x =>
                x.FullName.Contains(term) ||
                (x.UserName != null && x.UserName.Contains(term)) ||
                (x.Email != null && x.Email.Contains(term)));
        }

        var total = await q.CountAsync(ct);

        var users = await q
            .OrderBy(x => x.FullName)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var userIds = users.Select(u => u.Id).ToList();
        var roleLookup = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where userIds.Contains(ur.UserId)
            select new { ur.UserId, RoleName = r.Name ?? string.Empty })
            .ToListAsync(ct);

        var roleMap = roleLookup
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.First().RoleName);

        var items = users.Select(u => new UserDto(
            u.Id,
            u.UserName ?? string.Empty,
            u.FullName,
            u.Email ?? string.Empty,
            roleMap.GetValueOrDefault(u.Id, string.Empty),
            u.IsActive,
            u.MustChangePassword,
            u.CreatedAt,
            u.LastLoginAt)).ToList();

        return new PagedResult<UserDto>(items, total, page, size);
    }

    public async Task<UserDto> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new EntityNotFoundException(nameof(ApplicationUser), userId);

        var roles = await userManager.GetRolesAsync(user);
        return new UserDto(
            user.Id,
            user.UserName ?? string.Empty,
            user.FullName,
            user.Email ?? string.Empty,
            roles.FirstOrDefault() ?? string.Empty,
            user.IsActive,
            user.MustChangePassword,
            user.CreatedAt,
            user.LastLoginAt);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(createValidator, request, ct);

        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            Email = request.Email.Trim(),
            FullName = request.FullName.Trim(),
            EmailConfirmed = true,
            IsActive = true,
            MustChangePassword = request.MustChangePassword,
            CreatedAt = clock.UtcNow
        };

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var createResult = await userManager.CreateAsync(user, request.TemporaryPassword);
            EnsureIdentitySuccess(createResult);

            var roleResult = await userManager.AddToRoleAsync(user, request.Role);
            EnsureIdentitySuccess(roleResult);

            await audit.AddAuditEntryAsync(
                "CreateUser",
                nameof(ApplicationUser),
                user.Id,
                $"Created user '{user.UserName}' with role '{request.Role}'.",
                newValues: new { user.UserName, user.FullName, user.Email, request.Role, user.IsActive },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(user.Id, ct);
    }

    public async Task<UserDto> UpdateAsync(UpdateUserRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(updateValidator, request, ct);

        var user = await userManager.FindByIdAsync(request.Id)
            ?? throw new EntityNotFoundException(nameof(ApplicationUser), request.Id);

        var currentActorId = currentUser.EnsureAuthenticatedUserId();
        var existingRoles = await userManager.GetRolesAsync(user);
        var currentRole = existingRoles.FirstOrDefault() ?? string.Empty;

        if (user.Id == currentActorId && (!request.IsActive || request.Role != AppRoles.Owner))
        {
            throw new DomainValidationException("You cannot deactivate your own Owner account or remove your own Owner role.");
        }

        var oldSnapshot = new { user.FullName, user.Email, Role = currentRole, user.IsActive };

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            user.FullName = request.FullName.Trim();
            user.Email = request.Email.Trim();
            user.NormalizedEmail = userManager.NormalizeEmail(user.Email);
            user.IsActive = request.IsActive;
            user.LockoutEnabled = true;
            user.LockoutEnd = request.IsActive ? null : DateTimeOffset.MaxValue;

            EnsureIdentitySuccess(await userManager.UpdateAsync(user));

            if (!string.Equals(currentRole, request.Role, StringComparison.Ordinal))
            {
                if (existingRoles.Count > 0)
                {
                    EnsureIdentitySuccess(await userManager.RemoveFromRolesAsync(user, existingRoles));
                }
                EnsureIdentitySuccess(await userManager.AddToRoleAsync(user, request.Role));
                await userManager.UpdateSecurityStampAsync(user);
            }

            if (!request.IsActive)
            {
                await userManager.UpdateSecurityStampAsync(user);
            }

            await audit.AddAuditEntryAsync(
                "UpdateUser",
                nameof(ApplicationUser),
                user.Id,
                $"Updated user '{user.UserName}' (Role: {request.Role}, Active: {request.IsActive}).",
                oldValues: oldSnapshot,
                newValues: new { user.FullName, user.Email, Role = request.Role, user.IsActive },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(user.Id, ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(resetValidator, request, ct);

        var user = await userManager.FindByIdAsync(request.UserId)
            ?? throw new EntityNotFoundException(nameof(ApplicationUser), request.UserId);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            EnsureIdentitySuccess(await userManager.ResetPasswordAsync(user, token, request.NewTemporaryPassword));

            user.MustChangePassword = request.MustChangePassword;
            EnsureIdentitySuccess(await userManager.UpdateAsync(user));
            await userManager.UpdateSecurityStampAsync(user);

            await audit.AddAuditEntryAsync(
                "ResetUserPassword",
                nameof(ApplicationUser),
                user.Id,
                $"Reset password for user '{user.UserName}'.",
                newValues: new { user.MustChangePassword },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetActiveStatusAsync(string userId, bool isActive, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var currentActorId = currentUser.EnsureAuthenticatedUserId();
        if (userId == currentActorId && !isActive)
        {
            throw new DomainValidationException("You cannot deactivate your own active Owner account.");
        }

        var user = await userManager.FindByIdAsync(userId)
            ?? throw new EntityNotFoundException(nameof(ApplicationUser), userId);

        if (user.IsActive == isActive)
        {
            return;
        }

        user.IsActive = isActive;
        user.LockoutEnabled = true;
        user.LockoutEnd = isActive ? null : DateTimeOffset.MaxValue;

        EnsureIdentitySuccess(await userManager.UpdateAsync(user));
        await userManager.UpdateSecurityStampAsync(user);

        await audit.AddAuditEntryAsync(
            isActive ? "ActivateUser" : "DeactivateUser",
            nameof(ApplicationUser),
            user.Id,
            $"{(isActive ? "Activated" : "Deactivated")} user '{user.UserName}'.",
            oldValues: new { IsActive = !isActive },
            newValues: new { IsActive = isActive },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }

    private static void EnsureIdentitySuccess(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new DomainValidationException(errors);
        }
    }
}
