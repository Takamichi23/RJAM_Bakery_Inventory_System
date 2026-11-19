using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IRawMaterialService
{
    Task<PagedResult<RawMaterialDto>> GetPagedAsync(RawMaterialQuery query, CancellationToken ct = default);
    Task<RawMaterialDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<string> GetNextSkuAsync(CancellationToken ct = default);
    Task<RawMaterialDto> SaveAsync(SaveRawMaterialRequest request, CancellationToken ct = default);
    Task SetArchiveStatusAsync(int rawMaterialId, bool archive, CancellationToken ct = default);
    Task<InventoryMovementDto> AdjustStockAsync(RawMaterialAdjustmentRequest request, CancellationToken ct = default);
}

public sealed class RawMaterialService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<SaveRawMaterialRequest> saveValidator,
    IValidator<RawMaterialAdjustmentRequest> adjustValidator) : IRawMaterialService
{
    public Task<string> GetNextSkuAsync(CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();
        return SkuGenerator.ResolveSkuAsync(null, null, db.RawMaterials.AsNoTracking().Select(x => x.Sku), "RM", ct);
    }
    public async Task<PagedResult<RawMaterialDto>> GetPagedAsync(RawMaterialQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var page = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = db.RawMaterials.AsNoTracking();

        if (!query.IncludeArchived)
        {
            q = q.Where(x => !x.IsArchived);
        }

        if (query.OutOfStockOnly)
        {
            q = q.Where(x => x.CurrentQuantity <= 0);
        }
        else if (query.LowStockOnly)
        {
            q = q.Where(x => x.CurrentQuantity > 0 && x.CurrentQuantity <= x.MinStockLevel);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim();
            q = q.Where(x => x.Name.Contains(term) || x.Sku.Contains(term));
        }

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Sku,
                x.Name,
                x.Description,
                x.UnitId,
                UnitCode = x.Unit.Code,
                Dimension = x.Unit.Dimension.ToString(),
                x.PackToBaseRatio,
                x.CurrentQuantity,
                x.MinStockLevel,
                x.PreferredSupplierId,
                PreferredSupplierName = x.PreferredSupplier != null ? x.PreferredSupplier.Name : null,
                x.IsArchived,
                x.UpdatedAt
            })
            .ToListAsync(ct);

        var items = rows.Select(x => new RawMaterialDto(
            x.Id,
            x.Sku,
            x.Name,
            x.Description,
            x.UnitId,
            x.UnitCode,
            x.Dimension,
            x.PackToBaseRatio,
            x.CurrentQuantity,
            x.MinStockLevel,
            x.PreferredSupplierId,
            x.PreferredSupplierName,
            ComputeStockStatus(x.CurrentQuantity, x.MinStockLevel),
            x.IsArchived,
            x.UpdatedAt)).ToList();

        return new PagedResult<RawMaterialDto>(items, total, page, pageSize);
    }

    public async Task<RawMaterialDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var x = await db.RawMaterials.AsNoTracking()
            .Include(m => m.Unit)
            .Include(m => m.PreferredSupplier)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new EntityNotFoundException(nameof(RawMaterial), id);

        return new RawMaterialDto(
            x.Id,
            x.Sku,
            x.Name,
            x.Description,
            x.UnitId,
            x.Unit.Code,
            x.Unit.Dimension.ToString(),
            x.PackToBaseRatio,
            x.CurrentQuantity,
            x.MinStockLevel,
            x.PreferredSupplierId,
            x.PreferredSupplier?.Name,
            ComputeStockStatus(x.CurrentQuantity, x.MinStockLevel),
            x.IsArchived,
            x.UpdatedAt);
    }

    public async Task<RawMaterialDto> SaveAsync(SaveRawMaterialRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        await ValidationHelper.ValidateAndThrowAsync(saveValidator, request, ct);

        var unit = await db.UnitsOfMeasure.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.UnitId && x.IsActive, ct)
            ?? throw new DomainValidationException("Selected unit of measure does not exist.");

        if (request.PreferredSupplierId.HasValue)
        {
            var supplierExists = await db.Suppliers.AsNoTracking().AnyAsync(x => x.Id == request.PreferredSupplierId.Value, ct);
            if (!supplierExists)
            {
                throw new DomainValidationException("Selected preferred supplier does not exist.");
            }
        }

        var isCreate = !request.Id.HasValue || request.Id.Value == 0;
        RawMaterial entity;
        string normalizedSku;

        if (isCreate)
        {
            normalizedSku = await SkuGenerator.ResolveSkuAsync(
                request.Sku,
                existingEntitySku: null,
                db.RawMaterials.AsNoTracking().Select(x => x.Sku),
                "RM",
                ct);

            var skuConflict = await db.RawMaterials.AsNoTracking()
                .AnyAsync(x => x.Sku.ToLower() == normalizedSku.ToLower(), ct);
            if (skuConflict)
            {
                throw new DomainValidationException($"A raw material with SKU '{normalizedSku}' already exists.");
            }

            entity = new RawMaterial
            {
                Sku = normalizedSku,
                Name = request.Name.Trim(),
                Description = request.Description.Trim(),
                UnitId = unit.Id,
                PackToBaseRatio = request.PackToBaseRatio,
                CurrentQuantity = 0m,
                MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero),
                PreferredSupplierId = request.PreferredSupplierId,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            db.RawMaterials.Add(entity);
            await db.SaveChangesAsync(ct);

            await audit.AddAuditEntryAsync(
                "CreateRawMaterial",
                nameof(RawMaterial),
                entity.Id.ToString(),
                $"Registered raw material '{entity.Name}' (SKU: {entity.Sku}, Unit: {unit.Code}).",
                newValues: new { entity.Sku, entity.Name, entity.UnitId, entity.MinStockLevel },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var id = request.Id!.Value;
            entity = await db.RawMaterials.Include(x => x.Unit).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new EntityNotFoundException(nameof(RawMaterial), id);

            normalizedSku = await SkuGenerator.ResolveSkuAsync(
                request.Sku,
                existingEntitySku: entity.Sku,
                db.RawMaterials.AsNoTracking().Select(x => x.Sku),
                "RM",
                ct);

            var skuConflict = await db.RawMaterials.AsNoTracking()
                .AnyAsync(x => x.Sku.ToLower() == normalizedSku.ToLower() && x.Id != entity.Id, ct);
            if (skuConflict)
            {
                throw new DomainValidationException($"A raw material with SKU '{normalizedSku}' already exists.");
            }

            if (entity.UnitId != unit.Id && entity.Unit.Dimension != unit.Dimension)
            {
                throw new DomainValidationException(
                    $"Cannot change raw material unit dimension from '{entity.Unit.Dimension}' to '{unit.Dimension}' after registration.");
            }

            var oldSnapshot = new { entity.Sku, entity.Name, entity.UnitId, entity.MinStockLevel, entity.PackToBaseRatio };

            entity.Sku = normalizedSku;
            entity.Name = request.Name.Trim();
            entity.Description = request.Description.Trim();
            entity.PackToBaseRatio = request.PackToBaseRatio;
            entity.MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero);
            entity.PreferredSupplierId = request.PreferredSupplierId;
            entity.UpdatedAt = clock.UtcNow;

            await audit.AddAuditEntryAsync(
                "UpdateRawMaterial",
                nameof(RawMaterial),
                entity.Id.ToString(),
                $"Updated raw material '{entity.Name}' (SKU: {entity.Sku}).",
                oldValues: oldSnapshot,
                newValues: new { entity.Sku, entity.Name, entity.UnitId, entity.MinStockLevel, entity.PackToBaseRatio },
                ct: ct);
            await db.SaveChangesAsync(ct);
        }

        return await GetByIdAsync(entity.Id, ct);
    }

    public async Task SetArchiveStatusAsync(int rawMaterialId, bool archive, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var entity = await db.RawMaterials.FirstOrDefaultAsync(x => x.Id == rawMaterialId, ct)
            ?? throw new EntityNotFoundException(nameof(RawMaterial), rawMaterialId);

        if (entity.IsArchived == archive)
        {
            return;
        }

        entity.IsArchived = archive;
        entity.UpdatedAt = clock.UtcNow;

        await audit.AddAuditEntryAsync(
            archive ? "ArchiveRawMaterial" : "RestoreRawMaterial",
            nameof(RawMaterial),
            entity.Id.ToString(),
            $"{(archive ? "Archived" : "Restored")} raw material '{entity.Name}'.",
            oldValues: new { IsArchived = !archive },
            newValues: new { IsArchived = archive },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task<InventoryMovementDto> AdjustStockAsync(RawMaterialAdjustmentRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        await ValidationHelper.ValidateAndThrowAsync(adjustValidator, request, ct);

        var refKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"RMADJ-{Guid.NewGuid():N}"
            : request.IdempotencyKey.Trim();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var movement = await ledger.RecordRawMaterialMovementAsync(new RecordRawMaterialMovementRequest(
                RawMaterialId: request.RawMaterialId,
                QuantityDelta: request.QuantityDelta,
                UnitId: request.UnitId,
                SourceLocation: request.QuantityDelta > 0 ? InventoryLocation.Adjustment : InventoryLocation.Factory,
                DestinationLocation: request.QuantityDelta > 0 ? InventoryLocation.Factory : InventoryLocation.Adjustment,
                MovementType: request.IsOpeningStock ? InventoryMovementType.OpeningStock : InventoryMovementType.StockAdjustment,
                ReferenceType: "RawMaterialAdjustment",
                ReferenceId: refKey,
                ReferenceNumber: refKey[..Math.Min(24, refKey.Length)],
                Remarks: request.Reason,
                DeduplicationKey: $"RMADJ:{refKey}"), ct);

            await audit.AddAuditEntryAsync(
                request.IsOpeningStock ? "RawMaterialOpeningStock" : "RawMaterialStockAdjustment",
                nameof(RawMaterial),
                request.RawMaterialId.ToString(),
                $"Adjusted raw material #{request.RawMaterialId} by {movement.QuantityDelta:0.####} ({movement.PreviousQuantity:0.####} -> {movement.NewQuantity:0.####}). Reason: {request.Reason.Trim()}",
                oldValues: new { Quantity = movement.PreviousQuantity },
                newValues: new { Quantity = movement.NewQuantity, movement.MovementNumber },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var saved = await ledger.QueryMovementsAsync(new InventoryMovementQuery(
                ItemType: InventoryItemType.RawMaterial,
                RawMaterialId: request.RawMaterialId,
                ReferenceNumber: movement.MovementNumber,
                PageNumber: 1,
                PageSize: 1), ct);

            return saved.Items.First();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string ComputeStockStatus(decimal current, decimal min)
    {
        if (current <= 0) return "Out of Stock";
        if (current <= min) return "Low Stock";
        return "In Stock";
    }
}
