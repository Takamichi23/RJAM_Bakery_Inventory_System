namespace RjamBakery.Application.Contracts;

public sealed record SystemSettingDto(
    int Id,
    string SettingKey,
    string SettingValue,
    string Category,
    string Description,
    DateTime UpdatedAt,
    string? UpdatedByUserName);

public sealed record UpdateSystemSettingRequest(
    string SettingKey,
    string SettingValue);

public sealed record OperationalConfigDto(
    string BakeryName,
    string CurrencySymbol,
    string CurrencyCode,
    string FactoryOpenTime,
    string FactoryCloseTime,
    string StoreOpenTime,
    string DailyClosingTargetTime,
    bool EnforceOperatingWindow);

public sealed record UserDto(
    string Id,
    string UserName,
    string FullName,
    string Email,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed record CreateUserRequest(
    string UserName,
    string FullName,
    string Email,
    string Role,
    string TemporaryPassword,
    bool MustChangePassword = true);

public sealed record UpdateUserRequest(
    string Id,
    string FullName,
    string Email,
    string Role,
    bool IsActive);

public sealed record ResetPasswordRequest(
    string UserId,
    string NewTemporaryPassword,
    bool MustChangePassword = true);
