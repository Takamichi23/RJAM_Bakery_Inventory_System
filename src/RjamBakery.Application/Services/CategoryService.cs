using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Services;

public interface ICategoryService
{
    Task<IReadOnlyList<CategoryDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<CategoryDto> SaveAsync(SaveCategoryRequest request, CancellationToken ct = default);
    Task SetActiveStatusAsync(int categoryId, bool isActive, CancellationToken ct = default);
}

public sealed class CategoryService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IValidator<SaveCategoryRequest> validator) : ICategoryService
{
    public async Task<IReadOnlyList<CategoryDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();

        var q = db.ProductCategories.AsNoTracking();
        if (!includeInactive)
        {
            q = q.Where(x => x.IsActive);
        }

        return await q
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .Select(x => new CategoryDto(
                x.Id,
                x.Name,
                x.Description,
                x.DisplayOrder,
                x.IsActive,
                x.Products.Count(p => !p.IsArchived)))
            .ToListAsync(ct);
    }

    public async Task<CategoryDto> SaveAsync(SaveCategoryRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var normalizedName = request.Name.Trim();
        var duplicateExists = await db.ProductCategories.AsNoTracking()
            .AnyAsync(x => x.Name.ToLower() == normalizedName.ToLower() && (!request.Id.HasValue || x.Id != request.Id.Value), ct);

        if (duplicateExists)
        {
            throw new DomainValidationException($"A category named '{normalizedName}' already exists.");
        }

        ProductCategory entity;
        if (!request.Id.HasValue || request.Id.Value == 0)
        {
            entity = new ProductCategory
            {
                Name = normalizedName,
                Description = request.Description.Trim(),
                DisplayOrder = request.DisplayOrder,
                IsActive = request.IsActive
            };
            db.ProductCategories.Add(entity);
            await db.SaveChangesAsync(ct);

            await audit.AddAuditEntryAsync(
                "CreateCategory",
                nameof(ProductCategory),
                entity.Id.ToString(),
                $"Created product category '{entity.Name}'.",
                newValues: new { entity.Name, entity.DisplayOrder, entity.IsActive },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            entity = await db.ProductCategories.FirstOrDefaultAsync(x => x.Id == request.Id.Value, ct)
                ?? throw new EntityNotFoundException(nameof(ProductCategory), request.Id.Value);

            var oldSnapshot = new { entity.Name, entity.Description, entity.DisplayOrder, entity.IsActive };
            entity.Name = normalizedName;
            entity.Description = request.Description.Trim();
            entity.DisplayOrder = request.DisplayOrder;
            entity.IsActive = request.IsActive;

            await audit.AddAuditEntryAsync(
                "UpdateCategory",
                nameof(ProductCategory),
                entity.Id.ToString(),
                $"Updated product category '{entity.Name}'.",
                oldValues: oldSnapshot,
                newValues: new { entity.Name, entity.Description, entity.DisplayOrder, entity.IsActive },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }

        var productCount = await db.Products.AsNoTracking().CountAsync(p => p.CategoryId == entity.Id && !p.IsArchived, ct);
        return new CategoryDto(entity.Id, entity.Name, entity.Description, entity.DisplayOrder, entity.IsActive, productCount);
    }

    public async Task SetActiveStatusAsync(int categoryId, bool isActive, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var entity = await db.ProductCategories.FirstOrDefaultAsync(x => x.Id == categoryId, ct)
            ?? throw new EntityNotFoundException(nameof(ProductCategory), categoryId);

        if (entity.IsActive == isActive)
        {
            return;
        }

        var previous = entity.IsActive;
        entity.IsActive = isActive;

        await audit.AddAuditEntryAsync(
            isActive ? "ActivateCategory" : "DeactivateCategory",
            nameof(ProductCategory),
            entity.Id.ToString(),
            $"{(isActive ? "Activated" : "Deactivated")} category '{entity.Name}'.",
            oldValues: new { IsActive = previous },
            newValues: new { IsActive = isActive },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }
}
