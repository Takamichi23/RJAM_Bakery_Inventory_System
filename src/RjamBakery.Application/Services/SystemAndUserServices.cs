using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Services;

public interface ISystemSettingService
{
    Task<OperationalConfigDto> GetOperationalConfigAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken ct = default);
    Task<SystemSettingDto> UpdateAsync(UpdateSystemSettingRequest request, CancellationToken ct = default);
}

public interface IUserManagementService
{
    Task<PagedResult<UserDto>> GetPagedAsync(string? searchTerm = null, int pageNumber = 1, int pageSize = 25, CancellationToken ct = default);
    Task<UserDto> GetByIdAsync(string userId, CancellationToken ct = default);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto> UpdateAsync(UpdateUserRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
    Task SetActiveStatusAsync(string userId, bool isActive, CancellationToken ct = default);
}

public sealed class SystemSettingService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<UpdateSystemSettingRequest> validator) : ISystemSettingService
{
    public async Task<OperationalConfigDto> GetOperationalConfigAsync(CancellationToken ct = default)
    {
        var map = await db.SystemSettings.AsNoTracking()
            .ToDictionaryAsync(x => x.SettingKey, x => x.SettingValue, StringComparer.OrdinalIgnoreCase, ct);

        return new OperationalConfigDto(
            BakeryName: map.GetValueOrDefault("BakeryName", "RJAM Bakery"),
            CurrencySymbol: map.GetValueOrDefault("CurrencySymbol", "₱"),
            CurrencyCode: map.GetValueOrDefault("CurrencyCode", "PHP"),
            FactoryOpenTime: map.GetValueOrDefault("FactoryOpenTime", "06:00"),
            FactoryCloseTime: map.GetValueOrDefault("FactoryCloseTime", "18:00"),
            StoreOpenTime: map.GetValueOrDefault("StoreOpenTime", "06:00"),
            DailyClosingTargetTime: map.GetValueOrDefault("DailyClosingTargetTime", "19:00"),
            EnforceOperatingWindow: bool.TryParse(map.GetValueOrDefault("EnforceOperatingWindow", "false"), out var enforce) && enforce);
    }

    public async Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        return await db.SystemSettings.AsNoTracking()
            .OrderBy(x => x.Category)
            .ThenBy(x => x.SettingKey)
            .Select(x => new SystemSettingDto(
                x.Id,
                x.SettingKey,
                x.SettingValue,
                x.Category,
                x.Description,
                x.UpdatedAt,
                x.UpdatedByUser != null ? x.UpdatedByUser.FullName : null))
            .ToListAsync(ct);
    }

    public async Task<SystemSettingDto> UpdateAsync(UpdateSystemSettingRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var userId = currentUser.EnsureAuthenticatedUserId();
        var key = request.SettingKey.Trim();

        var setting = await db.SystemSettings.FirstOrDefaultAsync(x => x.SettingKey == key, ct)
            ?? throw new EntityNotFoundException(nameof(SystemSetting), key);

        var oldValue = setting.SettingValue;
        setting.SettingValue = request.SettingValue.Trim();
        setting.UpdatedAt = clock.UtcNow;
        setting.UpdatedByUserId = userId;

        await audit.AddAuditEntryAsync(
            "UpdateSystemSetting",
            nameof(SystemSetting),
            setting.Id.ToString(),
            $"Updated system setting '{setting.SettingKey}'.",
            oldValues: new { setting.SettingKey, Value = oldValue },
            newValues: new { setting.SettingKey, Value = setting.SettingValue },
            ct: ct);

        await db.SaveChangesAsync(ct);

        return new SystemSettingDto(
            setting.Id,
            setting.SettingKey,
            setting.SettingValue,
            setting.Category,
            setting.Description,
            setting.UpdatedAt,
            currentUser.FullName);
    }
}
