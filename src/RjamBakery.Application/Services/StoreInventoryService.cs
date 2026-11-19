using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IStoreInventoryService
{
    Task<PagedResult<StoreInventoryDto>> GetPagedAsync(StoreInventoryQuery query, CancellationToken ct = default);
    Task<StoreInventoryDto> GetByProductIdAsync(int productId, CancellationToken ct = default);
    Task UpdateThresholdAsync(UpdateStoreThresholdRequest request, CancellationToken ct = default);
    Task<PagedResult<StockAdjustmentRequestDto>> GetAdjustmentRequestsPagedAsync(ApprovalStatus? status = null, int pageNumber = 1, int pageSize = 25, CancellationToken ct = default);
    Task<StockAdjustmentRequestDto> RequestAdjustmentAsync(CreateStockAdjustmentRequest request, CancellationToken ct = default);
    Task<StockAdjustmentRequestDto> ReviewAdjustmentAsync(long requestId, ReviewStockAdjustmentRequest request, CancellationToken ct = default);
    Task<InventoryMovementDto> ApplyDirectStoreAdjustmentAsync(DirectStoreAdjustmentRequest request, CancellationToken ct = default);
}

public sealed class StoreInventoryService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<CreateStockAdjustmentRequest> requestValidator,
    IValidator<DirectStoreAdjustmentRequest> directValidator) : IStoreInventoryService
{
    public async Task<PagedResult<StoreInventoryDto>> GetPagedAsync(StoreInventoryQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.Products.AsNoTracking().Where(p => !p.IsArchived);

        if (query.CategoryId.HasValue)
        {
            q = q.Where(p => p.CategoryId == query.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim();
            q = q.Where(p => p.Name.Contains(term) || p.Sku.Contains(term));
        }

        if (query.OutOfStockOnly)
        {
            q = q.Where(p => p.StoreInventory == null || p.StoreInventory.CurrentQuantity <= 0m);
        }
        else if (query.LowStockOnly)
        {
            q = q.Where(p => p.StoreInventory != null
                             && p.StoreInventory.CurrentQuantity > 0m
                             && p.StoreInventory.CurrentQuantity <= p.StoreInventory.MinStockLevel);
        }

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderBy(p => p.Name)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(p => new
            {
                InventoryId = p.StoreInventory != null ? p.StoreInventory.Id : 0,
                ProductId = p.Id,
                p.Sku,
                p.Name,
                CategoryName = p.Category.Name,
                UnitCode = p.BaseUnit.Code,
                p.SellingPrice,
                CurrentQuantity = p.StoreInventory != null ? p.StoreInventory.CurrentQuantity : 0m,
                MinStockLevel = p.StoreInventory != null ? p.StoreInventory.MinStockLevel : p.MinStockLevel,
                LastReceivedAt = p.StoreInventory != null ? p.StoreInventory.LastReceivedAt : null,
                LastSoldAt = p.StoreInventory != null ? p.StoreInventory.LastSoldAt : null,
                UpdatedAt = p.StoreInventory != null ? p.StoreInventory.UpdatedAt : p.UpdatedAt
            })
            .ToListAsync(ct);

        var items = rows.Select(x => new StoreInventoryDto(
            x.InventoryId,
            x.ProductId,
            x.Sku,
            x.Name,
            x.CategoryName,
            x.UnitCode,
            x.SellingPrice,
            x.CurrentQuantity,
            x.CurrentQuantity,
            x.MinStockLevel,
            ResolveStockStatus(x.CurrentQuantity, x.MinStockLevel),
            x.LastReceivedAt,
            x.LastSoldAt,
            x.UpdatedAt)).ToList();

        return new PagedResult<StoreInventoryDto>(items, total, page, size);
    }

    public async Task<StoreInventoryDto> GetByProductIdAsync(int productId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var p = await db.Products.AsNoTracking()
            .Where(x => x.Id == productId)
            .Select(x => new
            {
                InventoryId = x.StoreInventory != null ? x.StoreInventory.Id : 0,
                ProductId = x.Id,
                x.Sku,
                x.Name,
                CategoryName = x.Category.Name,
                UnitCode = x.BaseUnit.Code,
                x.SellingPrice,
                CurrentQuantity = x.StoreInventory != null ? x.StoreInventory.CurrentQuantity : 0m,
                MinStockLevel = x.StoreInventory != null ? x.StoreInventory.MinStockLevel : x.MinStockLevel,
                LastReceivedAt = x.StoreInventory != null ? x.StoreInventory.LastReceivedAt : null,
                LastSoldAt = x.StoreInventory != null ? x.StoreInventory.LastSoldAt : null,
                UpdatedAt = x.StoreInventory != null ? x.StoreInventory.UpdatedAt : x.UpdatedAt
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(Product), productId);

        return new StoreInventoryDto(
            p.InventoryId,
            p.ProductId,
            p.Sku,
            p.Name,
            p.CategoryName,
            p.UnitCode,
            p.SellingPrice,
            p.CurrentQuantity,
            p.CurrentQuantity,
            p.MinStockLevel,
            ResolveStockStatus(p.CurrentQuantity, p.MinStockLevel),
            p.LastReceivedAt,
            p.LastSoldAt,
            p.UpdatedAt);
    }

    public async Task UpdateThresholdAsync(UpdateStoreThresholdRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        if (request.MinStockLevel < 0m)
        {
            throw new DomainValidationException("Minimum stock level cannot be negative.");
        }

        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), request.ProductId);

        var inv = await db.StoreInventories.FirstOrDefaultAsync(s => s.ProductId == request.ProductId, ct);
        decimal prevMin = product.MinStockLevel;

        if (inv is null)
        {
            inv = new StoreInventory
            {
                ProductId = request.ProductId,
                CurrentQuantity = 0m,
                MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero),
                UpdatedAt = clock.UtcNow
            };
            db.StoreInventories.Add(inv);
        }
        else
        {
            prevMin = inv.MinStockLevel;
            inv.MinStockLevel = decimal.Round(request.MinStockLevel, 4, MidpointRounding.AwayFromZero);
            inv.UpdatedAt = clock.UtcNow;
        }

        await audit.AddAuditEntryAsync(
            "UpdateStoreThreshold",
            nameof(StoreInventory),
            request.ProductId.ToString(),
            $"Updated store minimum stock level for '{product.Name}' ({prevMin:0.####} -> {inv.MinStockLevel:0.####}).",
            oldValues: new { MinStockLevel = prevMin },
            newValues: new { inv.MinStockLevel },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<StockAdjustmentRequestDto>> GetAdjustmentRequestsPagedAsync(
        ApprovalStatus? status = null,
        int pageNumber = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);
        var userId = currentUser.EnsureAuthenticatedUserId();

        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 200);

        var q = db.StockAdjustmentRequests.AsNoTracking();

        if (!currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            q = q.Where(x => x.RequestedByUserId == userId);
        }

        if (status.HasValue)
        {
            q = q.Where(x => x.ApprovalStatus == status.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await ProjectAdjustmentRequests(
            q.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<StockAdjustmentRequestDto>(items, total, page, size);
    }

    public async Task<StockAdjustmentRequestDto> RequestAdjustmentAsync(CreateStockAdjustmentRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(requestValidator, request, ct);

        // Enforce role-to-location scope
        if (currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            && request.Location != AdjustmentLocation.Store)
        {
            throw new UnauthorizedDomainActionException("Cashiers can only request adjustments for Store inventory.");
        }

        if (currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            && request.Location == AdjustmentLocation.Store)
        {
            throw new UnauthorizedDomainActionException("Factory staff cannot request adjustments for Store inventory.");
        }

        switch (request.Location)
        {
            case AdjustmentLocation.Store:
                if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == request.ProductId!.Value, ct))
                    throw new EntityNotFoundException(nameof(Product), request.ProductId!.Value);
                break;
            case AdjustmentLocation.RawMaterial:
                if (!await db.RawMaterials.AsNoTracking().AnyAsync(m => m.Id == request.RawMaterialId!.Value, ct))
                    throw new EntityNotFoundException(nameof(RawMaterial), request.RawMaterialId!.Value);
                break;
            case AdjustmentLocation.Factory:
                if (!await db.FactoryStockLots.AsNoTracking().AnyAsync(l => l.Id == request.FactoryStockLotId!.Value, ct))
                    throw new EntityNotFoundException(nameof(FactoryStockLot), request.FactoryStockLotId!.Value);
                break;
        }

        var refNumber = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"ADJ-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
            : request.IdempotencyKey.Trim();

        var entity = new StockAdjustmentRequest
        {
            ReferenceNumber = refNumber,
            Location = request.Location,
            ProductId = request.ProductId,
            RawMaterialId = request.RawMaterialId,
            FactoryStockLotId = request.FactoryStockLotId,
            QuantityDelta = decimal.Round(request.QuantityDelta, 4, MidpointRounding.AwayFromZero),
            Reason = request.Reason.Trim(),
            RequestedByUserId = userId,
            RequestedAt = clock.UtcNow,
            ApprovalStatus = ApprovalStatus.Pending
        };

        db.StockAdjustmentRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.AddAuditEntryAsync(
            "RequestStockAdjustment",
            nameof(StockAdjustmentRequest),
            entity.Id.ToString(),
            $"Submitted stock adjustment request {entity.ReferenceNumber} ({entity.Location}, Delta: {entity.QuantityDelta:0.####}). Reason: {entity.Reason}",
            newValues: new { entity.ReferenceNumber, entity.Location, entity.QuantityDelta },
            ct: ct);
        await db.SaveChangesAsync(ct);

        return await ProjectAdjustmentRequests(db.StockAdjustmentRequests.AsNoTracking().Where(x => x.Id == entity.Id)).FirstAsync(ct);
    }

    public async Task<StockAdjustmentRequestDto> ReviewAdjustmentAsync(
        long requestId,
        ReviewStockAdjustmentRequest request,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var userId = currentUser.EnsureAuthenticatedUserId();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var adj = await db.StockAdjustmentRequests
                .FirstOrDefaultAsync(x => x.Id == requestId, ct)
                ?? throw new EntityNotFoundException(nameof(StockAdjustmentRequest), requestId);

            if (adj.ApprovalStatus != ApprovalStatus.Pending)
            {
                throw new DuplicateTransactionException(adj.ReferenceNumber);
            }

            adj.ApprovedByUserId = userId;
            adj.ApprovedAt = clock.UtcNow;

            if (!request.Approve)
            {
                adj.ApprovalStatus = ApprovalStatus.Rejected;
                if (!string.IsNullOrWhiteSpace(request.ReviewNotes))
                {
                    adj.Reason = $"{adj.Reason} [Rejected: {request.ReviewNotes.Trim()}]";
                }

                await audit.AddAuditEntryAsync(
                    "RejectStockAdjustment",
                    nameof(StockAdjustmentRequest),
                    adj.Id.ToString(),
                    $"Rejected stock adjustment {adj.ReferenceNumber}.",
                    oldValues: new { ApprovalStatus = ApprovalStatus.Pending },
                    newValues: new { ApprovalStatus = ApprovalStatus.Rejected },
                    ct: ct);

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return await ProjectAdjustmentRequests(db.StockAdjustmentRequests.AsNoTracking().Where(x => x.Id == adj.Id)).FirstAsync(ct);
            }

            adj.ApprovalStatus = ApprovalStatus.Approved;
            var fullRemarks = string.IsNullOrWhiteSpace(request.ReviewNotes)
                ? adj.Reason
                : $"{adj.Reason} | Approved Note: {request.ReviewNotes.Trim()}";

            switch (adj.Location)
            {
                case AdjustmentLocation.Store:
                    await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                        ProductId: adj.ProductId!.Value,
                        QuantityDelta: adj.QuantityDelta,
                        SourceLocation: adj.QuantityDelta > 0 ? InventoryLocation.Adjustment : InventoryLocation.Store,
                        DestinationLocation: adj.QuantityDelta > 0 ? InventoryLocation.Store : InventoryLocation.Adjustment,
                        MovementType: InventoryMovementType.StockAdjustment,
                        ReferenceType: nameof(StockAdjustmentRequest),
                        ReferenceId: adj.Id.ToString(),
                        ReferenceNumber: adj.ReferenceNumber,
                        Remarks: fullRemarks,
                        DeduplicationKey: $"ApprovedAdj:Store:{adj.Id}"), ct);
                    break;

                case AdjustmentLocation.Factory:
                    await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                        FactoryStockLotId: adj.FactoryStockLotId!.Value,
                        QuantityDelta: adj.QuantityDelta,
                        SourceLocation: adj.QuantityDelta > 0 ? InventoryLocation.Adjustment : InventoryLocation.Factory,
                        DestinationLocation: adj.QuantityDelta > 0 ? InventoryLocation.Factory : InventoryLocation.Adjustment,
                        MovementType: InventoryMovementType.StockAdjustment,
                        ReferenceType: nameof(StockAdjustmentRequest),
                        ReferenceId: adj.Id.ToString(),
                        ReferenceNumber: adj.ReferenceNumber,
                        Remarks: fullRemarks,
                        DeduplicationKey: $"ApprovedAdj:Factory:{adj.Id}"), ct);
                    break;

                case AdjustmentLocation.RawMaterial:
                    var mat = await db.RawMaterials.AsNoTracking().FirstAsync(m => m.Id == adj.RawMaterialId!.Value, ct);
                    await ledger.RecordRawMaterialMovementAsync(new RecordRawMaterialMovementRequest(
                        RawMaterialId: adj.RawMaterialId!.Value,
                        QuantityDelta: adj.QuantityDelta,
                        UnitId: mat.UnitId,
                        SourceLocation: adj.QuantityDelta > 0 ? InventoryLocation.Adjustment : InventoryLocation.Factory,
                        DestinationLocation: adj.QuantityDelta > 0 ? InventoryLocation.Factory : InventoryLocation.Adjustment,
                        MovementType: InventoryMovementType.StockAdjustment,
                        ReferenceType: nameof(StockAdjustmentRequest),
                        ReferenceId: adj.Id.ToString(),
                        ReferenceNumber: adj.ReferenceNumber,
                        Remarks: fullRemarks,
                        DeduplicationKey: $"ApprovedAdj:RawMaterial:{adj.Id}"), ct);
                    break;
            }

            await audit.AddAuditEntryAsync(
                "ApproveStockAdjustment",
                nameof(StockAdjustmentRequest),
                adj.Id.ToString(),
                $"Approved stock adjustment {adj.ReferenceNumber} ({adj.Location}, Delta: {adj.QuantityDelta:0.####}).",
                oldValues: new { ApprovalStatus = ApprovalStatus.Pending },
                newValues: new { ApprovalStatus = ApprovalStatus.Approved },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await ProjectAdjustmentRequests(db.StockAdjustmentRequests.AsNoTracking().Where(x => x.Id == requestId)).FirstAsync(ct);
    }

    public async Task<InventoryMovementDto> ApplyDirectStoreAdjustmentAsync(DirectStoreAdjustmentRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(directValidator, request, ct);

        var refKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"STRADJ-{Guid.NewGuid():N}"[..24]
            : request.IdempotencyKey.Trim();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var adjRecord = new StockAdjustmentRequest
            {
                ReferenceNumber = refKey,
                Location = AdjustmentLocation.Store,
                ProductId = request.ProductId,
                QuantityDelta = decimal.Round(request.QuantityDelta, 4, MidpointRounding.AwayFromZero),
                Reason = request.Reason.Trim(),
                RequestedByUserId = userId,
                RequestedAt = clock.UtcNow,
                ApprovalStatus = ApprovalStatus.Approved,
                ApprovedByUserId = userId,
                ApprovedAt = clock.UtcNow
            };
            db.StockAdjustmentRequests.Add(adjRecord);

            var movement = await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                ProductId: request.ProductId,
                QuantityDelta: request.QuantityDelta,
                SourceLocation: request.QuantityDelta > 0 ? InventoryLocation.Adjustment : InventoryLocation.Store,
                DestinationLocation: request.QuantityDelta > 0 ? InventoryLocation.Store : InventoryLocation.Adjustment,
                MovementType: request.IsOpeningStock ? InventoryMovementType.OpeningStock : InventoryMovementType.StockAdjustment,
                ReferenceType: "StoreStockAdjustment",
                ReferenceId: refKey,
                ReferenceNumber: refKey,
                Remarks: request.Reason,
                DeduplicationKey: $"STRADJ:{refKey}"), ct);

            await audit.AddAuditEntryAsync(
                request.IsOpeningStock ? "StoreOpeningStock" : "StoreStockAdjustment",
                nameof(StoreInventory),
                request.ProductId.ToString(),
                $"Adjusted store inventory for product #{request.ProductId} by {movement.QuantityDelta:0.####} ({movement.PreviousQuantity:0.####} -> {movement.NewQuantity:0.####}). Reason: {request.Reason.Trim()}",
                oldValues: new { Quantity = movement.PreviousQuantity },
                newValues: new { Quantity = movement.NewQuantity, movement.MovementNumber },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var saved = await ledger.QueryMovementsAsync(new InventoryMovementQuery(
                ItemType: InventoryItemType.FinishedProduct,
                ProductId: request.ProductId,
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

    private static string ResolveStockStatus(decimal current, decimal min)
    {
        if (current <= 0m) return "Out of Stock";
        if (current <= min) return "Low Stock";
        return "In Stock";
    }

    private static IQueryable<StockAdjustmentRequestDto> ProjectAdjustmentRequests(IQueryable<StockAdjustmentRequest> source) =>
        source.Select(x => new StockAdjustmentRequestDto(
            x.Id,
            x.ReferenceNumber,
            x.Location,
            x.ProductId,
            x.Product != null ? x.Product.Name : null,
            x.RawMaterialId,
            x.RawMaterial != null ? x.RawMaterial.Name : null,
            x.FactoryStockLotId,
            x.QuantityDelta,
            x.Reason,
            x.RequestedByUser.FullName,
            x.RequestedAt,
            x.ApprovalStatus,
            x.ApprovedByUser != null ? x.ApprovedByUser.FullName : null,
            x.ApprovedAt));
}
