using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Services;

public interface IProductService
{
    Task<PagedResult<ProductDto>> GetPagedAsync(ProductQuery query, CancellationToken ct = default);
    Task<ProductDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<string> GetNextSkuAsync(CancellationToken ct = default);
    Task<ProductDto> SaveAsync(SaveProductRequest request, CancellationToken ct = default);
    Task SetArchiveStatusAsync(int productId, bool archive, CancellationToken ct = default);
    Task<IReadOnlyList<UnitOfMeasureDto>> GetUnitsOfMeasureAsync(CancellationToken ct = default);
}

public sealed class ProductService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<SaveProductRequest> validator) : IProductService
{
    public Task<string> GetNextSkuAsync(CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();
        return SkuGenerator.ResolveSkuAsync(null, null, db.Products.AsNoTracking().Select(x => x.Sku), "PRD", ct);
    }
    public async Task<PagedResult<ProductDto>> GetPagedAsync(ProductQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();

        var page = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = db.Products.AsNoTracking();

        if (!query.IncludeArchived)
        {
            q = q.Where(x => !x.IsArchived);
        }

        if (query.CategoryId.HasValue)
        {
            q = q.Where(x => x.CategoryId == query.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim();
            q = q.Where(x => x.Name.Contains(term) || x.Sku.Contains(term));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ProductDto(
                x.Id,
                x.CategoryId,
                x.Category.Name,
                x.Sku,
                x.Name,
                x.Description,
                x.BaseUnitId,
                x.BaseUnit.Code,
                x.SellingPrice,
                x.MinStockLevel,
                x.ShelfLifeDays,
                x.ImagePath,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<ProductDto>(items, total, page, pageSize);
    }

    public async Task<ProductDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();

        return await db.Products.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new ProductDto(
                x.Id,
                x.CategoryId,
                x.Category.Name,
                x.Sku,
                x.Name,
                x.Description,
                x.BaseUnitId,
                x.BaseUnit.Code,
                x.SellingPrice,
                x.MinStockLevel,
                x.ShelfLifeDays,
                x.ImagePath,
                x.IsArchived,
                x.CreatedAt,
                x.UpdatedAt))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(Product), id);
    }

    public async Task<ProductDto> SaveAsync(SaveProductRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var categoryExists = await db.ProductCategories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId && x.IsActive, ct);
        if (!categoryExists)
        {
            throw new DomainValidationException("Selected product category does not exist or is inactive.");
        }

        var unitExists = await db.UnitsOfMeasure.AsNoTracking().AnyAsync(x => x.Id == request.BaseUnitId && x.IsActive, ct);
        if (!unitExists)
        {
            throw new DomainValidationException("Selected base unit of measure does not exist.");
        }

        var isCreate = !request.Id.HasValue || request.Id.Value == 0;
        Product entity;
        string normalizedSku;

        if (isCreate)
        {
            normalizedSku = await SkuGenerator.ResolveSkuAsync(
                request.Sku,
                existingEntitySku: null,
                db.Products.AsNoTracking().Select(x => x.Sku),
                "PRD",
                ct);

            var skuConflict = await db.Products.AsNoTracking()
                .AnyAsync(x => x.Sku.ToLower() == normalizedSku.ToLower(), ct);
            if (skuConflict)
            {
                throw new DomainValidationException($"A product with SKU '{normalizedSku}' already exists.");
            }

            entity = new Product
            {
                CategoryId = request.CategoryId,
                Sku = normalizedSku,
                Name = request.Name.Trim(),
                Description = request.Description.Trim(),
                BaseUnitId = request.BaseUnitId,
                SellingPrice = decimal.Round(request.SellingPrice, 2, MidpointRounding.AwayFromZero),
                MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero),
                ShelfLifeDays = request.ShelfLifeDays,
                ImagePath = string.IsNullOrWhiteSpace(request.ImagePath) ? null : request.ImagePath.Trim(),
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.Products.Add(entity);
            await db.SaveChangesAsync(ct);

            await audit.AddAuditEntryAsync(
                "CreateProduct",
                nameof(Product),
                entity.Id.ToString(),
                $"Created product '{entity.Name}' (SKU: {entity.Sku}).",
                newValues: new { entity.Sku, entity.Name, entity.SellingPrice, entity.CategoryId },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var id = request.Id!.Value;
            entity = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new EntityNotFoundException(nameof(Product), id);

            normalizedSku = await SkuGenerator.ResolveSkuAsync(
                request.Sku,
                existingEntitySku: entity.Sku,
                db.Products.AsNoTracking().Select(x => x.Sku),
                "PRD",
                ct);

            var skuConflict = await db.Products.AsNoTracking()
                .AnyAsync(x => x.Sku.ToLower() == normalizedSku.ToLower() && x.Id != entity.Id, ct);
            if (skuConflict)
            {
                throw new DomainValidationException($"A product with SKU '{normalizedSku}' already exists.");
            }

            var oldSnapshot = new
            {
                entity.CategoryId,
                entity.Sku,
                entity.Name,
                entity.SellingPrice,
                entity.MinStockLevel,
                entity.ShelfLifeDays
            };

            entity.CategoryId = request.CategoryId;
            entity.Sku = normalizedSku;
            entity.Name = request.Name.Trim();
            entity.Description = request.Description.Trim();
            entity.BaseUnitId = request.BaseUnitId;
            entity.SellingPrice = decimal.Round(request.SellingPrice, 2, MidpointRounding.AwayFromZero);
            entity.MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero);
            entity.ShelfLifeDays = request.ShelfLifeDays;
            entity.ImagePath = string.IsNullOrWhiteSpace(request.ImagePath) ? null : request.ImagePath.Trim();
            entity.UpdatedAt = clock.UtcNow;

            await audit.AddAuditEntryAsync(
                "UpdateProduct",
                nameof(Product),
                entity.Id.ToString(),
                $"Updated product '{entity.Name}' (SKU: {entity.Sku}).",
                oldValues: oldSnapshot,
                newValues: new
                {
                    entity.CategoryId,
                    entity.Sku,
                    entity.Name,
                    entity.SellingPrice,
                    entity.MinStockLevel,
                    entity.ShelfLifeDays
                },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }

        return await GetByIdAsync(entity.Id, ct);
    }

    public async Task SetArchiveStatusAsync(int productId, bool archive, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner);

        var entity = await db.Products.FirstOrDefaultAsync(x => x.Id == productId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), productId);

        if (entity.IsArchived == archive)
        {
            return;
        }

        entity.IsArchived = archive;
        entity.UpdatedAt = clock.UtcNow;

        await audit.AddAuditEntryAsync(
            archive ? "ArchiveProduct" : "RestoreProduct",
            nameof(Product),
            entity.Id.ToString(),
            $"{(archive ? "Archived" : "Restored")} product '{entity.Name}' (SKU: {entity.Sku}).",
            oldValues: new { IsArchived = !archive },
            newValues: new { IsArchived = archive },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UnitOfMeasureDto>> GetUnitsOfMeasureAsync(CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();

        return await db.UnitsOfMeasure.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Dimension)
            .ThenBy(x => x.FactorToBase)
            .Select(x => new UnitOfMeasureDto(
                x.Id,
                x.Code,
                x.Name,
                x.Dimension.ToString(),
                x.FactorToBase,
                x.IsBaseUnit))
            .ToListAsync(ct);
    }
}
