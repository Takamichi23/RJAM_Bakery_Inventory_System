using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Services;

public interface IRecipeService
{
    Task<RecipeDto?> GetActiveByProductAsync(int productId, CancellationToken ct = default);
    Task<IReadOnlyList<RecipeDto>> GetHistoryByProductAsync(int productId, CancellationToken ct = default);
    Task<RecipeDto> SaveNewVersionAsync(SaveRecipeRequest request, CancellationToken ct = default);
    Task<ProductionRequirementPreviewDto> CalculateRequirementsAsync(int productId, decimal targetOutputQuantity, CancellationToken ct = default);
}

public sealed class RecipeService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    ISystemSettingService settingService,
    IDateTimeProvider clock,
    IValidator<SaveRecipeRequest> validator) : IRecipeService
{
    public async Task<RecipeDto?> GetActiveByProductAsync(int productId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        return await ProjectRecipes(db.Recipes.AsNoTracking().Where(r => r.ProductId == productId && r.IsActive))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<RecipeDto>> GetHistoryByProductAsync(int productId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        return await ProjectRecipes(
            db.Recipes.AsNoTracking()
                .Where(r => r.ProductId == productId)
                .OrderByDescending(r => r.VersionNumber))
            .ToListAsync(ct);
    }

    public async Task<RecipeDto> SaveNewVersionAsync(SaveRecipeRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var product = await db.Products
            .Include(p => p.BaseUnit)
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), request.ProductId);

        if (product.IsArchived)
        {
            throw new DomainValidationException($"Cannot create a recipe for archived product '{product.Name}'.");
        }

        var yieldUnit = await db.UnitsOfMeasure.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.YieldUnitId && u.IsActive, ct)
            ?? throw new DomainValidationException("Selected yield unit does not exist.");

        if (!UnitConversionHelper.AreCompatible(yieldUnit, product.BaseUnit))
        {
            throw new DomainValidationException(
                $"Recipe yield unit '{yieldUnit.Code}' ({yieldUnit.Dimension}) is incompatible with product base unit '{product.BaseUnit.Code}' ({product.BaseUnit.Dimension}).");
        }

        var ingredientMaterialIds = request.Ingredients.Select(i => i.RawMaterialId).ToList();
        if (ingredientMaterialIds.Count != ingredientMaterialIds.Distinct().Count())
        {
            throw new DomainValidationException("A recipe cannot contain duplicate raw material lines.");
        }

        var materials = await db.RawMaterials
            .Include(m => m.Unit)
            .Where(m => ingredientMaterialIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var unitIds = request.Ingredients.Select(i => i.UnitId).Distinct().ToList();
        var units = await db.UnitsOfMeasure.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id) && u.IsActive)
            .ToDictionaryAsync(u => u.Id, ct);

        foreach (var ing in request.Ingredients)
        {
            if (!materials.TryGetValue(ing.RawMaterialId, out var material) || material.IsArchived)
            {
                throw new DomainValidationException($"Raw material ({ing.RawMaterialId}) does not exist or is archived.");
            }

            if (!units.TryGetValue(ing.UnitId, out var ingUnit))
            {
                throw new DomainValidationException($"Ingredient unit ({ing.UnitId}) does not exist.");
            }

            // Validate unit compatibility and pack-to-base ratio upfront.
            _ = UnitConversionHelper.Convert(ing.QuantityRequired, ingUnit, material.Unit, material.PackToBaseRatio);
        }

        int newRecipeId;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var existingRecipes = await db.Recipes
                .Where(r => r.ProductId == product.Id)
                .ToListAsync(ct);

            foreach (var old in existingRecipes.Where(r => r.IsActive))
            {
                old.IsActive = false;
            }

            var nextVersion = existingRecipes.Count == 0 ? 1 : existingRecipes.Max(r => r.VersionNumber) + 1;

            var recipe = new Recipe
            {
                ProductId = product.Id,
                VersionNumber = nextVersion,
                BatchYieldQuantity = decimal.Round(request.BatchYieldQuantity, 4, MidpointRounding.AwayFromZero),
                YieldUnitId = yieldUnit.Id,
                IsActive = true,
                Notes = request.Notes.Trim(),
                CreatedByUserId = userId,
                CreatedAt = clock.UtcNow
            };

            foreach (var ing in request.Ingredients)
            {
                recipe.Ingredients.Add(new RecipeIngredient
                {
                    RawMaterialId = ing.RawMaterialId,
                    QuantityRequired = decimal.Round(ing.QuantityRequired, 4, MidpointRounding.AwayFromZero),
                    UnitId = ing.UnitId
                });
            }

            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(ct);
            newRecipeId = recipe.Id;

            await audit.AddAuditEntryAsync(
                "SaveRecipeVersion",
                nameof(Recipe),
                recipe.Id.ToString(),
                $"Created Recipe v{recipe.VersionNumber} for '{product.Name}' (Batch yield: {recipe.BatchYieldQuantity:0.####} {yieldUnit.Code}, {recipe.Ingredients.Count} ingredients).",
                newValues: new { product.Id, recipe.VersionNumber, recipe.BatchYieldQuantity, IngredientCount = recipe.Ingredients.Count },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await ProjectRecipes(db.Recipes.AsNoTracking().Where(r => r.Id == newRecipeId)).FirstAsync(ct);
    }

    public async Task<ProductionRequirementPreviewDto> CalculateRequirementsAsync(
        int productId,
        decimal targetOutputQuantity,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        if (targetOutputQuantity <= 0)
        {
            throw new DomainValidationException("Target production output quantity must be greater than zero.");
        }

        var product = await db.Products.AsNoTracking()
            .Include(p => p.BaseUnit)
            .FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), productId);

        var recipe = await db.Recipes.AsNoTracking()
            .Include(r => r.YieldUnit)
            .Include(r => r.Ingredients).ThenInclude(i => i.Unit)
            .Include(r => r.Ingredients).ThenInclude(i => i.RawMaterial).ThenInclude(m => m.Unit)
            .FirstOrDefaultAsync(r => r.ProductId == productId && r.IsActive, ct);

        var opConfig = await settingService.GetOperationalConfigAsync(ct);
        var outsideHours = IsOutsideWindow(clock.LocalNow, opConfig.FactoryOpenTime, opConfig.FactoryCloseTime);

        if (recipe is null)
        {
            return new ProductionRequirementPreviewDto(
                product.Id,
                product.Name,
                null,
                null,
                targetOutputQuantity,
                product.BaseUnit.Code,
                AllIngredientsSufficient: true,
                IsOutsideFactoryHours: outsideHours,
                Ingredients: []);
        }

        var targetInYieldUnit = UnitConversionHelper.Convert(targetOutputQuantity, product.BaseUnit, recipe.YieldUnit);
        var scale = targetInYieldUnit / recipe.BatchYieldQuantity;

        var requirements = new List<CalculatedIngredientRequirementDto>();
        foreach (var ing in recipe.Ingredients)
        {
            var scaledInRecipeUnit = decimal.Round(ing.QuantityRequired * scale, 4, MidpointRounding.AwayFromZero);
            var requiredInBaseUnit = UnitConversionHelper.Convert(
                scaledInRecipeUnit,
                ing.Unit,
                ing.RawMaterial.Unit,
                ing.RawMaterial.PackToBaseRatio);

            var available = ing.RawMaterial.CurrentQuantity;
            var sufficient = available >= requiredInBaseUnit;
            var shortfall = sufficient ? 0m : decimal.Round(requiredInBaseUnit - available, 4, MidpointRounding.AwayFromZero);

            requirements.Add(new CalculatedIngredientRequirementDto(
                ing.RawMaterialId,
                ing.RawMaterial.Sku,
                ing.RawMaterial.Name,
                scaledInRecipeUnit,
                ing.Unit.Code,
                requiredInBaseUnit,
                available,
                ing.RawMaterial.Unit.Code,
                sufficient,
                shortfall));
        }

        return new ProductionRequirementPreviewDto(
            product.Id,
            product.Name,
            recipe.Id,
            recipe.VersionNumber,
            targetOutputQuantity,
            product.BaseUnit.Code,
            AllIngredientsSufficient: requirements.All(r => r.IsSufficient),
            IsOutsideFactoryHours: outsideHours,
            Ingredients: requirements);
    }

    internal static bool IsOutsideWindow(DateTime localNow, string openTimeStr, string closeTimeStr)
    {
        if (!TimeOnly.TryParse(openTimeStr, out var openTime) ||
            !TimeOnly.TryParse(closeTimeStr, out var closeTime))
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(localNow);
        return now < openTime || now > closeTime;
    }

    private static IQueryable<RecipeDto> ProjectRecipes(IQueryable<Recipe> source) =>
        source.Select(r => new RecipeDto(
            r.Id,
            r.ProductId,
            r.Product.Sku,
            r.Product.Name,
            r.VersionNumber,
            r.BatchYieldQuantity,
            r.YieldUnitId,
            r.YieldUnit.Code,
            r.IsActive,
            r.Notes,
            r.CreatedByUser.FullName,
            r.CreatedAt,
            r.Ingredients.Select(i => new RecipeIngredientDto(
                i.Id,
                i.RawMaterialId,
                i.RawMaterial.Sku,
                i.RawMaterial.Name,
                i.QuantityRequired,
                i.UnitId,
                i.Unit.Code)).ToList()));
}
