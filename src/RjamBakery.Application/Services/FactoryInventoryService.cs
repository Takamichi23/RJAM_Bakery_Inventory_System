using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IFactoryInventoryService
{
    Task<PagedResult<FactoryStockLotDto>> GetLotsPagedAsync(FactoryLotQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<FactoryProductSummaryDto>> GetProductSummaryAsync(string? searchTerm = null, CancellationToken ct = default);
    Task<IReadOnlyList<FefoAllocationPreviewDto>> PreviewFefoAllocationAsync(int productId, decimal requestedQuantity, CancellationToken ct = default);
    Task MarkLotExpiredOrQuarantinedAsync(long lotId, FactoryLotStatus targetStatus, string reason, CancellationToken ct = default);
}

public sealed class FactoryInventoryService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock) : IFactoryInventoryService
{
    public async Task<PagedResult<FactoryStockLotDto>> GetLotsPagedAsync(FactoryLotQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var today = clock.TodayLocal;

        var q = db.FactoryStockLots.AsNoTracking();

        if (query.ProductId.HasValue)
        {
            q = q.Where(x => x.ProductId == query.ProductId.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(x => x.Status == query.Status.Value);
        }

        if (query.OnlyAvailable)
        {
            q = q.Where(x => x.Status == FactoryLotStatus.Available && (x.RemainingQuantity - x.ReservedQuantity) > 0);
        }

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderBy(x => x.ExpirationDate == null ? 1 : 0)
            .ThenBy(x => x.ExpirationDate)
            .ThenBy(x => x.ProductionDate)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new
            {
                x.Id,
                x.LotNumber,
                x.ProductId,
                ProductSku = x.Product.Sku,
                ProductName = x.Product.Name,
                UnitCode = x.Product.BaseUnit.Code,
                x.ProductionBatchId,
                BatchReference = x.ProductionBatch != null ? x.ProductionBatch.BatchReference : null,
                x.ProductionDate,
                x.ExpirationDate,
                x.InitialQuantity,
                x.RemainingQuantity,
                x.ReservedQuantity,
                x.Status
            })
            .ToListAsync(ct);

        var items = rows.Select(x => new FactoryStockLotDto(
            x.Id,
            x.LotNumber,
            x.ProductId,
            x.ProductSku,
            x.ProductName,
            x.UnitCode,
            x.ProductionBatchId,
            x.BatchReference,
            x.ProductionDate,
            x.ExpirationDate,
            x.InitialQuantity,
            x.RemainingQuantity,
            x.ReservedQuantity,
            decimal.Round(x.RemainingQuantity - x.ReservedQuantity, 4, MidpointRounding.AwayFromZero),
            x.Status,
            ResolveLotDisplayStatus(x.Status, x.RemainingQuantity, x.ReservedQuantity, x.ExpirationDate, today)))
            .ToList();

        return new PagedResult<FactoryStockLotDto>(items, total, page, size);
    }

    public async Task<IReadOnlyList<FactoryProductSummaryDto>> GetProductSummaryAsync(string? searchTerm = null, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var productsQuery = db.Products.AsNoTracking().Where(p => !p.IsArchived);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            productsQuery = productsQuery.Where(p => p.Name.Contains(term) || p.Sku.Contains(term));
        }

        var products = await productsQuery
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Name,
                UnitCode = p.BaseUnit.Code,
                p.MinStockLevel
            })
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var lotStats = await db.FactoryStockLots.AsNoTracking()
            .Where(l => l.Status == FactoryLotStatus.Available && l.RemainingQuantity > 0)
            .GroupBy(l => l.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Remaining = g.Sum(x => x.RemainingQuantity),
                Reserved = g.Sum(x => x.ReservedQuantity),
                Count = g.Count(),
                EarliestExpiry = g.Min(x => x.ExpirationDate)
            })
            .ToDictionaryAsync(x => x.ProductId, ct);

        return products.Select(p =>
        {
            lotStats.TryGetValue(p.Id, out var stat);
            var remaining = stat?.Remaining ?? 0m;
            var reserved = stat?.Reserved ?? 0m;
            var available = decimal.Round(remaining - reserved, 4, MidpointRounding.AwayFromZero);

            var status = available <= 0m
                ? "Out of Stock"
                : available <= p.MinStockLevel
                    ? "Low Stock"
                    : "In Stock";

            return new FactoryProductSummaryDto(
                p.Id,
                p.Sku,
                p.Name,
                p.UnitCode,
                remaining,
                reserved,
                available,
                p.MinStockLevel,
                stat?.Count ?? 0,
                stat?.EarliestExpiry,
                status);
        }).ToList();
    }

    public async Task<IReadOnlyList<FefoAllocationPreviewDto>> PreviewFefoAllocationAsync(
        int productId,
        decimal requestedQuantity,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        if (requestedQuantity <= 0)
        {
            throw new DomainValidationException("Requested allocation quantity must be greater than zero.");
        }

        var today = clock.TodayLocal;
        var product = await db.Products.AsNoTracking()
            .Include(p => p.BaseUnit)
            .FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new EntityNotFoundException(nameof(Product), productId);

        var eligibleLots = await db.FactoryStockLots.AsNoTracking()
            .Where(l => l.ProductId == productId
                        && l.Status == FactoryLotStatus.Available
                        && (l.RemainingQuantity - l.ReservedQuantity) > 0
                        && (l.ExpirationDate == null || l.ExpirationDate >= today))
            .OrderBy(l => l.ExpirationDate == null ? 1 : 0)
            .ThenBy(l => l.ExpirationDate)
            .ThenBy(l => l.ProductionDate)
            .ThenBy(l => l.Id)
            .ToListAsync(ct);

        var allocations = new List<FefoAllocationPreviewDto>();
        var remainingNeeded = requestedQuantity;

        foreach (var lot in eligibleLots)
        {
            if (remainingNeeded <= 0)
            {
                break;
            }

            var lotAvailable = lot.RemainingQuantity - lot.ReservedQuantity;
            var take = Math.Min(lotAvailable, remainingNeeded);
            allocations.Add(new FefoAllocationPreviewDto(
                lot.Id,
                lot.LotNumber,
                lot.ProductionDate,
                lot.ExpirationDate,
                take));
            remainingNeeded = decimal.Round(remainingNeeded - take, 4, MidpointRounding.AwayFromZero);
        }

        if (remainingNeeded > 0)
        {
            var totalAvailable = eligibleLots.Sum(l => l.RemainingQuantity - l.ReservedQuantity);
            throw new InsufficientStockException(product.Name, totalAvailable, requestedQuantity, product.BaseUnit.Code);
        }

        return allocations;
    }

    public async Task MarkLotExpiredOrQuarantinedAsync(
        long lotId,
        FactoryLotStatus targetStatus,
        string reason,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        if (targetStatus is not (FactoryLotStatus.Expired or FactoryLotStatus.Quarantined))
        {
            throw new DomainValidationException("Target status must be Expired or Quarantined.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException("A reason is required to expire or quarantine a factory stock lot.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var lot = await db.FactoryStockLots
                .Include(l => l.Product)
                .FirstOrDefaultAsync(l => l.Id == lotId, ct)
                ?? throw new EntityNotFoundException(nameof(FactoryStockLot), lotId);

            if (lot.Status == targetStatus)
            {
                return;
            }

            if (lot.ReservedQuantity > 0)
            {
                throw new DomainValidationException($"Lot {lot.LotNumber} currently has {lot.ReservedQuantity:0.####} reserved units and cannot be changed.");
            }

            var prevStatus = lot.Status;
            var qtyToWriteOff = lot.RemainingQuantity;

            if (qtyToWriteOff > 0)
            {
                var movementType = targetStatus == FactoryLotStatus.Expired
                    ? InventoryMovementType.ExpiredProduct
                    : InventoryMovementType.DamagedProduct;

                await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                    FactoryStockLotId: lot.Id,
                    QuantityDelta: -qtyToWriteOff,
                    SourceLocation: InventoryLocation.Factory,
                    DestinationLocation: InventoryLocation.WasteDisposal,
                    MovementType: movementType,
                    ReferenceType: nameof(FactoryStockLot),
                    ReferenceId: lot.Id.ToString(),
                    ReferenceNumber: lot.LotNumber,
                    Remarks: $"{targetStatus}: {reason.Trim()}",
                    DeduplicationKey: $"LotWriteOff:{targetStatus}:{lot.Id}:{Guid.NewGuid():N}"), ct);
            }

            lot.Status = targetStatus;
            lot.UpdatedAt = clock.UtcNow;

            await audit.AddAuditEntryAsync(
                "UpdateFactoryLotStatus",
                nameof(FactoryStockLot),
                lot.Id.ToString(),
                $"Marked factory lot {lot.LotNumber} ('{lot.Product.Name}') as {targetStatus} (Written off: {qtyToWriteOff:0.####}). Reason: {reason.Trim()}",
                oldValues: new { Status = prevStatus, Remaining = qtyToWriteOff },
                newValues: new { Status = targetStatus, Remaining = lot.RemainingQuantity },
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

    private static string ResolveLotDisplayStatus(
        FactoryLotStatus status,
        decimal remaining,
        decimal reserved,
        DateOnly? expirationDate,
        DateOnly today)
    {
        if (status == FactoryLotStatus.Expired || (expirationDate.HasValue && expirationDate.Value < today && remaining > 0))
            return "Expired";
        if (status == FactoryLotStatus.Quarantined)
            return "Quarantined";
        if (status == FactoryLotStatus.Depleted || remaining <= 0)
            return "Depleted";
        if (remaining - reserved <= 0)
            return "Fully Reserved";
        if (expirationDate.HasValue && expirationDate.Value <= today.AddDays(1))
            return "Expiring Soon";
        return "Available";
    }
}
