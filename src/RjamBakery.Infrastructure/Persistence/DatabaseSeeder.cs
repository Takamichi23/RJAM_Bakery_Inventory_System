using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Infrastructure.Persistence;

public sealed class DatabaseSeeder(
    AppDbContext db,
    RoleManager<IdentityRole> roleManager)
{
    public async Task SeedReferenceDefaultsAsync(CancellationToken ct = default)
    {
        foreach (var roleName in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
            }
        }

        await SeedUnitsOfMeasureAsync(ct);
        await SeedStarterCategoriesAsync(ct);
        await SeedDefaultSettingsAsync(ct);
    }

    private async Task SeedUnitsOfMeasureAsync(CancellationToken ct)
    {
        var desiredUnits = new[]
        {
            new UnitOfMeasure { Code = "g", Name = "Gram", Dimension = UnitDimension.Mass, FactorToBase = 1m, IsBaseUnit = true },
            new UnitOfMeasure { Code = "kg", Name = "Kilogram", Dimension = UnitDimension.Mass, FactorToBase = 1000m, IsBaseUnit = false },
            new UnitOfMeasure { Code = "mL", Name = "Milliliter", Dimension = UnitDimension.Volume, FactorToBase = 1m, IsBaseUnit = true },
            new UnitOfMeasure { Code = "L", Name = "Liter", Dimension = UnitDimension.Volume, FactorToBase = 1000m, IsBaseUnit = false },
            new UnitOfMeasure { Code = "pcs", Name = "Pieces", Dimension = UnitDimension.Count, FactorToBase = 1m, IsBaseUnit = true },
            new UnitOfMeasure { Code = "pack", Name = "Pack", Dimension = UnitDimension.Count, FactorToBase = 1m, IsBaseUnit = false }
        };

        var existingCodes = await db.UnitsOfMeasure.AsNoTracking().Select(x => x.Code).ToListAsync(ct);
        var missing = desiredUnits.Where(u => !existingCodes.Contains(u.Code, StringComparer.OrdinalIgnoreCase)).ToList();

        if (missing.Count > 0)
        {
            db.UnitsOfMeasure.AddRange(missing);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedStarterCategoriesAsync(CancellationToken ct)
    {
        if (await db.ProductCategories.AnyAsync(ct))
        {
            return;
        }

        db.ProductCategories.AddRange(
            new ProductCategory { Name = "Bread", Description = "Daily baked loaves, rolls, and pandesal.", DisplayOrder = 1, IsActive = true },
            new ProductCategory { Name = "Pastries", Description = "Croissants, danishes, ensaymada, and flaky baked goods.", DisplayOrder = 2, IsActive = true },
            new ProductCategory { Name = "Cakes", Description = "Whole cakes, sliced cakes, and special occasion bakes.", DisplayOrder = 3, IsActive = true },
            new ProductCategory { Name = "Cookies", Description = "Baked cookies, bars, and biscuits.", DisplayOrder = 4, IsActive = true },
            new ProductCategory { Name = "Other Bakery Products", Description = "Specialty and seasonal bakery items.", DisplayOrder = 5, IsActive = true }
        );
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedDefaultSettingsAsync(CancellationToken ct)
    {
        var defaults = new[]
        {
            new SystemSetting { SettingKey = "BakeryName", SettingValue = "RJAM Bakery", Category = "General", Description = "Display name shown on dashboards, receipts, and reports." },
            new SystemSetting { SettingKey = "CurrencySymbol", SettingValue = "₱", Category = "POS", Description = "Currency symbol for prices and POS receipts." },
            new SystemSetting { SettingKey = "CurrencyCode", SettingValue = "PHP", Category = "POS", Description = "ISO currency code." },
            new SystemSetting { SettingKey = "FactoryOpenTime", SettingValue = "06:00", Category = "Schedule", Description = "Configured daily factory production start time (24h HH:mm)." },
            new SystemSetting { SettingKey = "FactoryCloseTime", SettingValue = "18:00", Category = "Schedule", Description = "Configured daily factory production end time (24h HH:mm)." },
            new SystemSetting { SettingKey = "StoreOpenTime", SettingValue = "06:00", Category = "Schedule", Description = "Configured daily store opening time (24h HH:mm)." },
            new SystemSetting { SettingKey = "DailyClosingTargetTime", SettingValue = "19:00", Category = "Schedule", Description = "Target evening physical stock count time (24h HH:mm)." },
            new SystemSetting { SettingKey = "EnforceOperatingWindow", SettingValue = "false", Category = "Schedule", Description = "When true, warns or restricts out-of-window transactions per policy." }
        };

        var existingKeys = await db.SystemSettings.AsNoTracking().Select(x => x.SettingKey).ToListAsync(ct);
        var missing = defaults.Where(s => !existingKeys.Contains(s.SettingKey, StringComparer.OrdinalIgnoreCase)).ToList();

        if (missing.Count > 0)
        {
            db.SystemSettings.AddRange(missing);
            await db.SaveChangesAsync(ct);
        }
    }
}
