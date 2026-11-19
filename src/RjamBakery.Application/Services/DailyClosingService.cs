using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IDailyClosingService
{
    Task<DailyClosingDto> PreviewClosingAsync(DateOnly operatingDate, CancellationToken ct = default);
    Task<DailyClosingDto> SubmitPhysicalCountAsync(SubmitDailyClosingRequest request, CancellationToken ct = default);
    Task<DailyClosingDto> ApproveAndFinalizeAsync(long closingId, FinalizeDailyClosingRequest request, CancellationToken ct = default);
    Task<PagedResult<DailyClosingDto>> GetHistoryAsync(DateOnly? fromDate = null, DateOnly? toDate = null, int pageNumber = 1, int pageSize = 25, CancellationToken ct = default);
}

public sealed class DailyClosingService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    ISystemSettingService settingService,
    IDateTimeProvider clock,
    IValidator<SubmitDailyClosingRequest> submitValidator) : IDailyClosingService
{
    public async Task<DailyClosingDto> PreviewClosingAsync(DateOnly operatingDate, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var existing = await ProjectClosings(db.DailyClosings.AsNoTracking().Where(c => c.OperatingDate == operatingDate))
            .FirstOrDefaultAsync(ct);

        if (existing is not null && existing.Status == DailyClosingStatus.Finalized)
        {
            return existing;
        }

        var opConfig = await settingService.GetOperationalConfigAsync(ct);
        var beforeTarget = IsBeforeTargetTime(clock.LocalNow, opConfig.DailyClosingTargetTime);

        var computed = await ComputeExpectedBalancesAsync(operatingDate, clock.UtcNow, ct);
        if (existing is not null)
        {
            var actualMap = existing.Items.ToDictionary(i => i.ProductId);
            var merged = computed.Select(c =>
            {
                actualMap.TryGetValue(c.ProductId, out var prevInput);
                var actual = prevInput?.ActualClosingStock ?? c.ExpectedClosingStock;
                var variance = decimal.Round(actual - c.ExpectedClosingStock, 4, MidpointRounding.AwayFromZero);
                return c with
                {
                    Id = prevInput?.Id ?? 0,
                    ActualClosingStock = actual,
                    VarianceQuantity = variance,
                    VarianceReason = prevInput?.VarianceReason ?? string.Empty
                };
            }).ToList();

            return existing with { IsBeforeTargetClosingTime = beforeTarget, Items = merged };
        }

        return new DailyClosingDto(
            0,
            $"CLS-{operatingDate:yyyyMMdd}-DRAFT",
            operatingDate,
            clock.UtcNow,
            null,
            DailyClosingStatus.Open,
            null,
            null,
            null,
            string.Empty,
            beforeTarget,
            computed);
    }

    public async Task<DailyClosingDto> SubmitPhysicalCountAsync(SubmitDailyClosingRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);
        var actorId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(submitValidator, request, ct);

        if (request.Items.Select(i => i.ProductId).Distinct().Count() != request.Items.Count)
        {
            throw new DomainValidationException("Duplicate product lines are not allowed in daily closing counts.");
        }

        long closingId;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var existing = await db.DailyClosings
                .Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.OperatingDate == request.OperatingDate, ct);

            if (existing is not null && existing.Status == DailyClosingStatus.Finalized)
            {
                throw new DuplicateTransactionException(existing.ClosingNumber);
            }

            var snapshotCutoffUtc = clock.UtcNow;
            var expectedLines = await ComputeExpectedBalancesAsync(request.OperatingDate, snapshotCutoffUtc, ct);
            var inputMap = request.Items.ToDictionary(i => i.ProductId);

            foreach (var expected in expectedLines)
            {
                if (!inputMap.ContainsKey(expected.ProductId))
                {
                    throw new DomainValidationException(
                        $"Physical count must include product '{expected.ProductName}' ({expected.ProductSku}).");
                }
            }

            var closing = existing;
            if (closing is null)
            {
                closing = new DailyClosing
                {
                    ClosingNumber = $"CLS-{request.OperatingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
                    OperatingDate = request.OperatingDate,
                    OpenedAt = snapshotCutoffUtc
                };
                db.DailyClosings.Add(closing);
            }
            else
            {
                db.DailyClosingItems.RemoveRange(closing.Items);
                closing.Items.Clear();
            }

            closing.ClosedAt = snapshotCutoffUtc;
            closing.SubmittedByUserId = actorId;
            closing.Notes = request.Notes.Trim();

            bool hasAnyVariance = false;
            foreach (var exp in expectedLines)
            {
                var countInput = inputMap[exp.ProductId];
                var actual = decimal.Round(countInput.ActualClosingStock, 4, MidpointRounding.AwayFromZero);
                var variance = decimal.Round(actual - exp.ExpectedClosingStock, 4, MidpointRounding.AwayFromZero);

                if (variance != 0m)
                {
                    hasAnyVariance = true;
                    if (string.IsNullOrWhiteSpace(countInput.VarianceReason))
                    {
                        throw new DomainValidationException(
                            $"A variance explanation is required for '{exp.ProductName}' (Expected: {exp.ExpectedClosingStock:0.####}, Actual: {actual:0.####}).");
                    }
                }

                closing.Items.Add(new DailyClosingItem
                {
                    ProductId = exp.ProductId,
                    OpeningStock = exp.OpeningStock,
                    ConfirmedDeliveries = exp.ConfirmedDeliveries,
                    ApprovedRestockedReturns = exp.ApprovedRestockedReturns,
                    TotalSold = exp.TotalSold,
                    RecordedWaste = exp.RecordedWaste,
                    ApprovedAdjustments = exp.ApprovedAdjustments,
                    ExpectedClosingStock = exp.ExpectedClosingStock,
                    ActualClosingStock = actual,
                    VarianceQuantity = variance,
                    VarianceReason = countInput.VarianceReason?.Trim() ?? string.Empty,
                    CarryoverToNextDay = 0m
                });
            }

            closing.Status = hasAnyVariance
                ? DailyClosingStatus.VarianceReviewPending
                : DailyClosingStatus.Submitted;

            await db.SaveChangesAsync(ct);
            closingId = closing.Id;

            await audit.AddAuditEntryAsync(
                "SubmitDailyClosingCount",
                nameof(DailyClosing),
                closing.Id.ToString(),
                $"Submitted physical closing count {closing.ClosingNumber} for {closing.OperatingDate:yyyy-MM-dd} (Status: {closing.Status}, Variances: {closing.Items.Count(i => i.VarianceQuantity != 0)}).",
                newValues: new { closing.ClosingNumber, closing.Status, ItemCount = closing.Items.Count },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await ProjectClosings(db.DailyClosings.AsNoTracking().Where(c => c.Id == closingId)).FirstAsync(ct);
    }

    public async Task<DailyClosingDto> ApproveAndFinalizeAsync(
        long closingId,
        FinalizeDailyClosingRequest request,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var actorId = currentUser.EnsureAuthenticatedUserId();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var closing = await db.DailyClosings
                .Include(c => c.Items).ThenInclude(i => i.Product)
                .FirstOrDefaultAsync(c => c.Id == closingId, ct)
                ?? throw new EntityNotFoundException(nameof(DailyClosing), closingId);

            if (closing.Status == DailyClosingStatus.Finalized)
            {
                throw new DuplicateTransactionException(closing.ClosingNumber);
            }

            if (closing.Status is not (DailyClosingStatus.Submitted or DailyClosingStatus.VarianceReviewPending))
            {
                throw new DomainValidationException($"Daily closing {closing.ClosingNumber} must be submitted before finalization.");
            }

            foreach (var item in closing.Items)
            {
                if (item.VarianceQuantity != 0m && request.ApproveVarianceAdjustments)
                {
                    var refNum = $"CLSVAR-{closing.OperatingDate:yyyyMMdd}-{item.ProductId}";
                    var reason = string.IsNullOrWhiteSpace(request.ApprovalNotes)
                        ? $"Daily closing variance ({closing.ClosingNumber}): {item.VarianceReason}"
                        : $"Daily closing variance ({closing.ClosingNumber}): {item.VarianceReason} | Manager Note: {request.ApprovalNotes.Trim()}";

                    db.StockAdjustmentRequests.Add(new StockAdjustmentRequest
                    {
                        ReferenceNumber = refNum,
                        Location = AdjustmentLocation.Store,
                        ProductId = item.ProductId,
                        QuantityDelta = item.VarianceQuantity,
                        Reason = reason[..Math.Min(300, reason.Length)],
                        RequestedByUserId = closing.SubmittedByUserId ?? actorId,
                        RequestedAt = closing.ClosedAt ?? clock.UtcNow,
                        ApprovalStatus = ApprovalStatus.Approved,
                        ApprovedByUserId = actorId,
                        ApprovedAt = clock.UtcNow,
                        DailyClosingId = closing.Id
                    });

                    await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                        ProductId: item.ProductId,
                        QuantityDelta: item.VarianceQuantity,
                        SourceLocation: item.VarianceQuantity > 0 ? InventoryLocation.Adjustment : InventoryLocation.Store,
                        DestinationLocation: item.VarianceQuantity > 0 ? InventoryLocation.Store : InventoryLocation.Adjustment,
                        MovementType: InventoryMovementType.StockAdjustment,
                        ReferenceType: "DailyClosingVariance",
                        ReferenceId: closing.Id.ToString(),
                        ReferenceNumber: closing.ClosingNumber,
                        Remarks: reason[..Math.Min(500, reason.Length)],
                        DeduplicationKey: $"DailyClosingVar:Closing:{closing.Id}:PROD:{item.ProductId}"), ct);
                }

                var currentStoreQty = await db.StoreInventories.AsNoTracking()
                    .Where(s => s.ProductId == item.ProductId)
                    .Select(s => s.CurrentQuantity)
                    .FirstOrDefaultAsync(ct);

                item.CarryoverToNextDay = currentStoreQty;
            }

            var finalTimestamp = clock.UtcNow;
            closing.Status = DailyClosingStatus.Finalized;
            closing.ApprovedByUserId = actorId;
            closing.ApprovedAt = finalTimestamp;
            closing.ClosedAt = finalTimestamp;

            if (!string.IsNullOrWhiteSpace(request.ApprovalNotes))
            {
                closing.Notes = string.IsNullOrWhiteSpace(closing.Notes)
                    ? $"Approval: {request.ApprovalNotes.Trim()}"
                    : $"{closing.Notes} | Approval: {request.ApprovalNotes.Trim()}";
            }

            await audit.AddAuditEntryAsync(
                "FinalizeDailyClosing",
                nameof(DailyClosing),
                closing.Id.ToString(),
                $"Finalized daily closing {closing.ClosingNumber} for {closing.OperatingDate:yyyy-MM-dd} (Variance Adjustments Applied: {request.ApproveVarianceAdjustments}).",
                oldValues: new { Status = DailyClosingStatus.VarianceReviewPending },
                newValues: new { Status = DailyClosingStatus.Finalized, closing.ApprovedAt, request.ApproveVarianceAdjustments },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await ProjectClosings(db.DailyClosings.AsNoTracking().Where(c => c.Id == closingId)).FirstAsync(ct);
    }

    public async Task<PagedResult<DailyClosingDto>> GetHistoryAsync(
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        int pageNumber = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 200);

        var q = db.DailyClosings.AsNoTracking();
        if (fromDate.HasValue) q = q.Where(x => x.OperatingDate >= fromDate.Value);
        if (toDate.HasValue) q = q.Where(x => x.OperatingDate <= toDate.Value);

        var total = await q.CountAsync(ct);
        var items = await ProjectClosings(
            q.OrderByDescending(x => x.OperatingDate)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<DailyClosingDto>(items, total, page, size);
    }

    private async Task<List<DailyClosingItemDto>> ComputeExpectedBalancesAsync(
        DateOnly operatingDate,
        DateTime cutoffUtc,
        CancellationToken ct)
    {
        var priorFinalized = await db.DailyClosings.AsNoTracking()
            .Include(c => c.Items)
            .Where(c => c.Status == DailyClosingStatus.Finalized && c.OperatingDate < operatingDate)
            .OrderByDescending(c => c.OperatingDate)
            .FirstOrDefaultAsync(ct);

        var finalizedClosingIdsUpToPrior = priorFinalized is null
            ? new HashSet<string>()
            : (await db.DailyClosings.AsNoTracking()
                .Where(c => c.Status == DailyClosingStatus.Finalized && c.OperatingDate <= priorFinalized.OperatingDate)
                .Select(c => c.Id.ToString())
                .ToListAsync(ct)).ToHashSet();

        var openingByProduct = priorFinalized?.Items.ToDictionary(i => i.ProductId, i => i.CarryoverToNextDay)
            ?? new Dictionary<int, decimal>();

        var windowStartUtc = priorFinalized?.ClosedAt ?? priorFinalized?.ApprovedAt;

        var movementsQuery = db.InventoryMovements.AsNoTracking()
            .Where(m => m.ItemType == InventoryItemType.FinishedProduct
                        && m.FactoryStockLotId == null
                        && m.ProductId.HasValue
                        && (m.SourceLocation == InventoryLocation.Store || m.DestinationLocation == InventoryLocation.Store)
                        && m.OccurredAt <= cutoffUtc);

        if (windowStartUtc.HasValue)
        {
            movementsQuery = movementsQuery.Where(m => m.OccurredAt > windowStartUtc.Value);
        }

        var movements = await movementsQuery.ToListAsync(ct);

        // Exclude any variance adjustment belonging to an already-finalized prior closing so it never leaks across day boundary.
        if (finalizedClosingIdsUpToPrior.Count > 0)
        {
            movements = movements
                .Where(m => !(m.ReferenceType == "DailyClosingVariance" && finalizedClosingIdsUpToPrior.Contains(m.ReferenceId)))
                .ToList();
        }

        var movedProductIds = movements.Select(m => m.ProductId!.Value).ToHashSet();
        var openingProductIds = openingByProduct.Where(kv => kv.Value != 0m).Select(kv => kv.Key).ToHashSet();

        var products = await db.Products.AsNoTracking()
            .Include(p => p.BaseUnit)
            .Include(p => p.StoreInventory)
            .Where(p => !p.IsArchived
                        || (p.StoreInventory != null && p.StoreInventory.CurrentQuantity != 0m)
                        || movedProductIds.Contains(p.Id)
                        || openingProductIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var movesByProduct = movements.GroupBy(m => m.ProductId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<DailyClosingItemDto>();

        foreach (var prod in products)
        {
            movesByProduct.TryGetValue(prod.Id, out var pMoves);
            pMoves ??= [];

            decimal opening = priorFinalized is not null
                ? openingByProduct.GetValueOrDefault(prod.Id, 0m)
                : pMoves.Where(m => m.MovementType == InventoryMovementType.OpeningStock).Sum(m => m.QuantityDelta);

            decimal deliveries = pMoves.Where(m => m.MovementType == InventoryMovementType.StoreReceiving).Sum(m => m.QuantityDelta);
            decimal restockedReturns = pMoves.Where(m => m.MovementType == InventoryMovementType.CustomerReturn).Sum(m => m.QuantityDelta);

            // Net Sales = PosSale outflows (positive count) minus Void/Refund TransactionReversal inflows
            decimal grossSold = pMoves.Where(m => m.MovementType == InventoryMovementType.PosSale).Sum(m => Math.Abs(m.QuantityDelta));
            decimal reversedSales = pMoves.Where(m => m.MovementType == InventoryMovementType.TransactionReversal).Sum(m => m.QuantityDelta);
            decimal netSold = decimal.Round(grossSold - reversedSales, 4, MidpointRounding.AwayFromZero);

            decimal waste = pMoves.Where(m => m.MovementType is InventoryMovementType.DamagedProduct
                                                or InventoryMovementType.SpoiledProduct
                                                or InventoryMovementType.ExpiredProduct)
                                  .Sum(m => Math.Abs(m.QuantityDelta));

            decimal adjustments = pMoves.Where(m => m.MovementType == InventoryMovementType.StockAdjustment).Sum(m => m.QuantityDelta);
            if (priorFinalized is not null)
            {
                adjustments += pMoves.Where(m => m.MovementType == InventoryMovementType.OpeningStock).Sum(m => m.QuantityDelta);
            }

            decimal expected = decimal.Round(
                opening + deliveries + restockedReturns - netSold - waste + adjustments,
                4,
                MidpointRounding.AwayFromZero);

            rows.Add(new DailyClosingItemDto(
                0,
                prod.Id,
                prod.Sku,
                prod.Name,
                prod.BaseUnit.Code,
                opening,
                deliveries,
                restockedReturns,
                netSold,
                waste,
                adjustments,
                expected,
                expected,
                0m,
                string.Empty,
                0m));
        }

        return rows;
    }

    private static bool IsBeforeTargetTime(DateTime localNow, string targetTimeStr)
    {
        if (!TimeOnly.TryParse(targetTimeStr, out var target))
        {
            return false;
        }

        return TimeOnly.FromDateTime(localNow) < target;
    }

    private static IQueryable<DailyClosingDto> ProjectClosings(IQueryable<DailyClosing> source) =>
        source.Select(c => new DailyClosingDto(
            c.Id,
            c.ClosingNumber,
            c.OperatingDate,
            c.OpenedAt,
            c.ClosedAt,
            c.Status,
            c.SubmittedByUser != null ? c.SubmittedByUser.FullName : null,
            c.ApprovedByUser != null ? c.ApprovedByUser.FullName : null,
            c.ApprovedAt,
            c.Notes,
            false,
            c.Items.OrderBy(i => i.Product.Name).Select(i => new DailyClosingItemDto(
                i.Id,
                i.ProductId,
                i.Product.Sku,
                i.Product.Name,
                i.Product.BaseUnit.Code,
                i.OpeningStock,
                i.ConfirmedDeliveries,
                i.ApprovedRestockedReturns,
                i.TotalSold,
                i.RecordedWaste,
                i.ApprovedAdjustments,
                i.ExpectedClosingStock,
                i.ActualClosingStock,
                i.VarianceQuantity,
                i.VarianceReason,
                i.CarryoverToNextDay)).ToList()));
}
