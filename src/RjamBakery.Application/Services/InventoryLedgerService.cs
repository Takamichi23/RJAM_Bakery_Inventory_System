using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IInventoryLedgerService
{
    Task<InventoryMovement> RecordRawMaterialMovementAsync(
        RecordRawMaterialMovementRequest request,
        CancellationToken ct = default);

    Task<InventoryMovement> RecordFactoryLotMovementAsync(
        RecordFactoryLotMovementRequest request,
        CancellationToken ct = default);

    Task<InventoryMovement> RecordStoreMovementAsync(
        RecordStoreMovementRequest request,
        CancellationToken ct = default);

    Task<PagedResult<InventoryMovementDto>> QueryMovementsAsync(
        InventoryMovementQuery query,
        CancellationToken ct = default);

    Task<LedgerReconciliationResult> VerifyBalanceIntegrityAsync(CancellationToken ct = default);
}

public sealed class InventoryLedgerService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock) : IInventoryLedgerService
{
    public async Task<InventoryMovement> RecordRawMaterialMovementAsync(
        RecordRawMaterialMovementRequest request,
        CancellationToken ct = default)
    {
        var userId = currentUser.EnsureAuthenticatedUserId();
        if (request.QuantityDelta == 0)
        {
            throw new DomainValidationException("Inventory movement quantity delta cannot be zero.");
        }

        var dedupKey = ResolveDeduplicationKey(
            request.DeduplicationKey,
            request.MovementType,
            request.ReferenceType,
            request.ReferenceId,
            $"RM:{request.RawMaterialId}");

        return await ExecuteAtomicallyAsync(async () =>
        {
            await EnsureNotDuplicateAsync(dedupKey, request.ReferenceNumber, ct);

            var material = await db.RawMaterials
                .Include(x => x.Unit)
                .FirstOrDefaultAsync(x => x.Id == request.RawMaterialId, ct)
                ?? throw new EntityNotFoundException(nameof(RawMaterial), request.RawMaterialId);

            var sourceUnit = request.UnitId == material.UnitId
                ? material.Unit
                : await db.UnitsOfMeasure.FirstOrDefaultAsync(x => x.Id == request.UnitId, ct)
                  ?? throw new EntityNotFoundException(nameof(UnitOfMeasure), request.UnitId);

            var convertedDelta = UnitConversionHelper.Convert(
                request.QuantityDelta,
                sourceUnit,
                material.Unit,
                material.PackToBaseRatio);

            if (convertedDelta == 0)
            {
                throw new DomainValidationException("Converted quantity delta is zero and cannot be posted to the ledger.");
            }

            var previousQty = material.CurrentQuantity;
            var newQty = decimal.Round(previousQty + convertedDelta, 4, MidpointRounding.AwayFromZero);

            if (newQty < 0 && !request.AllowNegativeStockOverride)
            {
                throw new InsufficientStockException(material.Name, previousQty, Math.Abs(convertedDelta), material.Unit.Code);
            }

            material.CurrentQuantity = newQty;
            material.UpdatedAt = clock.UtcNow;

            var movement = new InventoryMovement
            {
                MovementNumber = GenerateMovementNumber(),
                DeduplicationKey = dedupKey,
                ItemType = InventoryItemType.RawMaterial,
                RawMaterialId = material.Id,
                SourceLocation = request.SourceLocation,
                DestinationLocation = request.DestinationLocation,
                MovementType = request.MovementType,
                QuantityDelta = convertedDelta,
                AbsoluteQuantity = Math.Abs(convertedDelta),
                UnitId = material.UnitId,
                PreviousQuantity = previousQty,
                NewQuantity = newQty,
                ReferenceType = request.ReferenceType,
                ReferenceId = request.ReferenceId,
                ReferenceNumber = request.ReferenceNumber,
                OccurredAt = clock.UtcNow,
                ResponsibleUserId = userId,
                Remarks = request.Remarks.Trim()
            };

            db.InventoryMovements.Add(movement);
            await SaveWithConcurrencyGuardAsync(dedupKey, ct);
            return movement;
        }, ct);
    }

    public async Task<InventoryMovement> RecordFactoryLotMovementAsync(
        RecordFactoryLotMovementRequest request,
        CancellationToken ct = default)
    {
        var userId = currentUser.EnsureAuthenticatedUserId();
        if (request.QuantityDelta == 0)
        {
            throw new DomainValidationException("Inventory movement quantity delta cannot be zero.");
        }

        var dedupKey = ResolveDeduplicationKey(
            request.DeduplicationKey,
            request.MovementType,
            request.ReferenceType,
            request.ReferenceId,
            $"LOT:{request.FactoryStockLotId}");

        return await ExecuteAtomicallyAsync(async () =>
        {
            await EnsureNotDuplicateAsync(dedupKey, request.ReferenceNumber, ct);

            var lot = await db.FactoryStockLots
                .Include(x => x.Product)
                .FirstOrDefaultAsync(x => x.Id == request.FactoryStockLotId, ct)
                ?? throw new EntityNotFoundException(nameof(FactoryStockLot), request.FactoryStockLotId);

            var previousQty = lot.RemainingQuantity;
            var newQty = decimal.Round(previousQty + request.QuantityDelta, 4, MidpointRounding.AwayFromZero);

            if (newQty < 0 && !request.AllowNegativeStockOverride)
            {
                throw new InsufficientStockException(
                    $"{lot.Product.Name} (Lot {lot.LotNumber})",
                    previousQty,
                    Math.Abs(request.QuantityDelta),
                    "unit");
            }

            lot.RemainingQuantity = newQty;
            lot.UpdatedAt = clock.UtcNow;
            if (newQty <= 0 && lot.Status == FactoryLotStatus.Available)
            {
                lot.Status = FactoryLotStatus.Depleted;
            }
            else if (newQty > 0 && lot.Status == FactoryLotStatus.Depleted)
            {
                lot.Status = FactoryLotStatus.Available;
            }

            var movement = new InventoryMovement
            {
                MovementNumber = GenerateMovementNumber(),
                DeduplicationKey = dedupKey,
                ItemType = InventoryItemType.FinishedProduct,
                ProductId = lot.ProductId,
                FactoryStockLotId = lot.Id,
                SourceLocation = request.SourceLocation,
                DestinationLocation = request.DestinationLocation,
                MovementType = request.MovementType,
                QuantityDelta = request.QuantityDelta,
                AbsoluteQuantity = Math.Abs(request.QuantityDelta),
                UnitId = lot.Product.BaseUnitId,
                PreviousQuantity = previousQty,
                NewQuantity = newQty,
                ReferenceType = request.ReferenceType,
                ReferenceId = request.ReferenceId,
                ReferenceNumber = request.ReferenceNumber,
                OccurredAt = clock.UtcNow,
                ResponsibleUserId = userId,
                Remarks = request.Remarks.Trim()
            };

            db.InventoryMovements.Add(movement);
            await SaveWithConcurrencyGuardAsync(dedupKey, ct);
            return movement;
        }, ct);
    }

    public async Task<InventoryMovement> RecordStoreMovementAsync(
        RecordStoreMovementRequest request,
        CancellationToken ct = default)
    {
        var userId = currentUser.EnsureAuthenticatedUserId();
        if (request.QuantityDelta == 0)
        {
            throw new DomainValidationException("Inventory movement quantity delta cannot be zero.");
        }

        var dedupKey = ResolveDeduplicationKey(
            request.DeduplicationKey,
            request.MovementType,
            request.ReferenceType,
            request.ReferenceId,
            $"STORE:{request.ProductId}");

        return await ExecuteAtomicallyAsync(async () =>
        {
            await EnsureNotDuplicateAsync(dedupKey, request.ReferenceNumber, ct);

            var product = await db.Products
                .Include(x => x.BaseUnit)
                .FirstOrDefaultAsync(x => x.Id == request.ProductId, ct)
                ?? throw new EntityNotFoundException(nameof(Product), request.ProductId);

            var storeInv = await db.StoreInventories
                .FirstOrDefaultAsync(x => x.ProductId == request.ProductId, ct);

            if (storeInv is null)
            {
                storeInv = new StoreInventory
                {
                    ProductId = product.Id,
                    CurrentQuantity = 0m,
                    MinStockLevel = product.MinStockLevel,
                    UpdatedAt = clock.UtcNow
                };
                db.StoreInventories.Add(storeInv);
            }

            var previousQty = storeInv.CurrentQuantity;
            var newQty = decimal.Round(previousQty + request.QuantityDelta, 4, MidpointRounding.AwayFromZero);

            if (newQty < 0 && !request.AllowNegativeStockOverride)
            {
                throw new InsufficientStockException(product.Name, previousQty, Math.Abs(request.QuantityDelta), product.BaseUnit.Code);
            }

            storeInv.CurrentQuantity = newQty;
            storeInv.UpdatedAt = clock.UtcNow;
            if (request.UpdateLastReceivedAt)
            {
                storeInv.LastReceivedAt = clock.UtcNow;
            }
            if (request.UpdateLastSoldAt)
            {
                storeInv.LastSoldAt = clock.UtcNow;
            }

            var movement = new InventoryMovement
            {
                MovementNumber = GenerateMovementNumber(),
                DeduplicationKey = dedupKey,
                ItemType = InventoryItemType.FinishedProduct,
                ProductId = product.Id,
                SourceLocation = request.SourceLocation,
                DestinationLocation = request.DestinationLocation,
                MovementType = request.MovementType,
                QuantityDelta = request.QuantityDelta,
                AbsoluteQuantity = Math.Abs(request.QuantityDelta),
                UnitId = product.BaseUnitId,
                PreviousQuantity = previousQty,
                NewQuantity = newQty,
                ReferenceType = request.ReferenceType,
                ReferenceId = request.ReferenceId,
                ReferenceNumber = request.ReferenceNumber,
                OccurredAt = clock.UtcNow,
                ResponsibleUserId = userId,
                Remarks = request.Remarks.Trim()
            };

            db.InventoryMovements.Add(movement);
            await SaveWithConcurrencyGuardAsync(dedupKey, ct);
            return movement;
        }, ct);
    }

    public async Task<PagedResult<InventoryMovementDto>> QueryMovementsAsync(
        InventoryMovementQuery query,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);

        var page = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = db.InventoryMovements.AsNoTracking();

        // Scope ledger visibility strictly by role when user is Baker or Cashier.
        if (currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            q = q.Where(x =>
                x.ItemType == InventoryItemType.RawMaterial ||
                x.SourceLocation == InventoryLocation.Factory ||
                x.DestinationLocation == InventoryLocation.Factory);
        }
        else if (currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            q = q.Where(x =>
                x.SourceLocation == InventoryLocation.Store ||
                x.DestinationLocation == InventoryLocation.Store);
        }

        if (query.ItemType.HasValue)
        {
            q = q.Where(x => x.ItemType == query.ItemType.Value);
        }

        if (query.RawMaterialId.HasValue)
        {
            q = q.Where(x => x.RawMaterialId == query.RawMaterialId.Value);
        }

        if (query.ProductId.HasValue)
        {
            q = q.Where(x => x.ProductId == query.ProductId.Value);
        }

        if (query.MovementType.HasValue)
        {
            q = q.Where(x => x.MovementType == query.MovementType.Value);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(x => x.OccurredAt >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(x => x.OccurredAt <= query.ToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.ReferenceNumber))
        {
            var r = query.ReferenceNumber.Trim();
            q = q.Where(x => x.ReferenceNumber.Contains(r) || x.MovementNumber.Contains(r));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new InventoryMovementDto(
                x.Id,
                x.MovementNumber,
                x.ItemType,
                x.RawMaterialId,
                x.ProductId,
                x.RawMaterial != null ? x.RawMaterial.Name : (x.Product != null ? x.Product.Name : "Unknown"),
                x.RawMaterial != null ? x.RawMaterial.Sku : (x.Product != null ? x.Product.Sku : string.Empty),
                x.FactoryStockLotId,
                x.SourceLocation,
                x.DestinationLocation,
                x.MovementType,
                x.QuantityDelta,
                x.AbsoluteQuantity,
                x.Unit.Code,
                x.PreviousQuantity,
                x.NewQuantity,
                x.ReferenceType,
                x.ReferenceId,
                x.ReferenceNumber,
                x.OccurredAt,
                x.ResponsibleUserId,
                x.ResponsibleUser.FullName,
                x.Remarks))
            .ToListAsync(ct);

        return new PagedResult<InventoryMovementDto>(items, total, page, pageSize);
    }

    public async Task<LedgerReconciliationResult> VerifyBalanceIntegrityAsync(CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var mismatches = new List<LedgerMismatchDto>();
        int checkedCount = 0;

        var rawSums = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.ItemType == InventoryItemType.RawMaterial && x.RawMaterialId.HasValue)
            .GroupBy(x => x.RawMaterialId!.Value)
            .Select(g => new { RawMaterialId = g.Key, Total = g.Sum(x => x.QuantityDelta) })
            .ToDictionaryAsync(x => x.RawMaterialId, x => x.Total, ct);

        var rawMaterials = await db.RawMaterials.AsNoTracking().ToListAsync(ct);
        foreach (var rm in rawMaterials)
        {
            checkedCount++;
            var ledgerSum = rawSums.GetValueOrDefault(rm.Id, 0m);
            if (rm.CurrentQuantity != ledgerSum)
            {
                mismatches.Add(new LedgerMismatchDto("RawMaterial", rm.Id, rm.Name, rm.CurrentQuantity, ledgerSum));
            }
        }

        var lotSums = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.FactoryStockLotId.HasValue)
            .GroupBy(x => x.FactoryStockLotId!.Value)
            .Select(g => new { LotId = g.Key, Total = g.Sum(x => x.QuantityDelta) })
            .ToDictionaryAsync(x => x.LotId, x => x.Total, ct);

        var lots = await db.FactoryStockLots.AsNoTracking().Include(x => x.Product).ToListAsync(ct);
        foreach (var lot in lots)
        {
            checkedCount++;
            var ledgerSum = lotSums.GetValueOrDefault(lot.Id, 0m);
            if (lot.RemainingQuantity != ledgerSum)
            {
                mismatches.Add(new LedgerMismatchDto("FactoryStockLot", lot.Id, $"{lot.Product.Name} ({lot.LotNumber})", lot.RemainingQuantity, ledgerSum));
            }
        }

        var storeSums = await db.InventoryMovements
            .AsNoTracking()
            .Where(x => x.ItemType == InventoryItemType.FinishedProduct
                        && x.FactoryStockLotId == null
                        && x.ProductId.HasValue
                        && (x.SourceLocation == InventoryLocation.Store || x.DestinationLocation == InventoryLocation.Store))
            .GroupBy(x => x.ProductId!.Value)
            .Select(g => new { ProductId = g.Key, Total = g.Sum(x => x.QuantityDelta) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Total, ct);

        var storeBalances = await db.StoreInventories.AsNoTracking().Include(x => x.Product).ToListAsync(ct);
        foreach (var inv in storeBalances)
        {
            checkedCount++;
            var ledgerSum = storeSums.GetValueOrDefault(inv.ProductId, 0m);
            if (inv.CurrentQuantity != ledgerSum)
            {
                mismatches.Add(new LedgerMismatchDto("StoreInventory", inv.ProductId, inv.Product.Name, inv.CurrentQuantity, ledgerSum));
            }
        }

        return new LedgerReconciliationResult(mismatches.Count == 0, checkedCount, mismatches);
    }

    private async Task EnsureNotDuplicateAsync(string dedupKey, string referenceNumber, CancellationToken ct)
    {
        var exists = await db.InventoryMovements.AsNoTracking()
            .AnyAsync(x => x.DeduplicationKey == dedupKey, ct);
        if (exists)
        {
            throw new DuplicateTransactionException(string.IsNullOrWhiteSpace(referenceNumber) ? dedupKey : referenceNumber);
        }
    }

    private async Task<T> ExecuteAtomicallyAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (db.HasActiveTransaction)
        {
            return await operation();
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var result = await operation();
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task SaveWithConcurrencyGuardAsync(string dedupKey, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("Inventory balance changed concurrently. Please reload and retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new DuplicateTransactionException(dedupKey);
        }
    }

    private static string ResolveDeduplicationKey(
        string? explicitKey,
        InventoryMovementType movementType,
        string referenceType,
        string referenceId,
        string bucketSuffix)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Trim();
        }

        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId))
        {
            throw new DomainValidationException("A ReferenceType and ReferenceId (or DeduplicationKey) are required for every inventory movement.");
        }

        return $"{movementType}:{referenceType.Trim()}:{referenceId.Trim()}:{bucketSuffix}";
    }

    private string GenerateMovementNumber()
        => $"MOV-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
