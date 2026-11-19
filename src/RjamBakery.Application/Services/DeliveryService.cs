using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IDeliveryService
{
    Task<PagedResult<DeliveryDto>> GetPagedAsync(DeliveryQuery query, CancellationToken ct = default);
    Task<DeliveryDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<DeliveryDto> CreateDraftAsync(CreateDeliveryRequest request, CancellationToken ct = default);
    Task<DeliveryDto> PrepareForDispatchAsync(long deliveryId, CancellationToken ct = default);
    Task<DeliveryDto> DispatchAsync(long deliveryId, DispatchDeliveryRequest? request = null, CancellationToken ct = default);
    Task CancelAsync(long deliveryId, string reason, CancellationToken ct = default);
    Task<DeliveryDto> ConfirmReceivingAsync(long deliveryId, ConfirmDeliveryReceivingRequest request, CancellationToken ct = default);
    Task<DeliveryDto> ReconcileDiscrepancyAsync(long deliveryId, ReconcileDeliveryRequest request, CancellationToken ct = default);
}

public sealed class DeliveryService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<CreateDeliveryRequest> createValidator,
    IValidator<ConfirmDeliveryReceivingRequest> receiveValidator,
    IValidator<ReconcileDeliveryRequest> reconcileValidator) : IDeliveryService
{
    public async Task<PagedResult<DeliveryDto>> GetPagedAsync(DeliveryQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.Deliveries.AsNoTracking();

        if (query.Status.HasValue)
        {
            q = q.Where(x => x.Status == query.Status.Value);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(x => x.CreatedAt >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(x => x.CreatedAt <= query.ToUtc.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await ProjectDeliveries(
            q.OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<DeliveryDto>(items, total, page, size);
    }

    public async Task<DeliveryDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);

        return await ProjectDeliveries(db.Deliveries.AsNoTracking().Where(x => x.Id == id))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(Delivery), id);
    }

    public async Task<DeliveryDto> CreateDraftAsync(CreateDeliveryRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(createValidator, request, ct);

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products
            .Include(p => p.BaseUnit)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var today = clock.TodayLocal;
        var availableByProduct = await db.FactoryStockLots.AsNoTracking()
            .Where(l => productIds.Contains(l.ProductId)
                        && l.Status == FactoryLotStatus.Available
                        && (l.RemainingQuantity - l.ReservedQuantity) > 0
                        && (l.ExpirationDate == null || l.ExpirationDate >= today))
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Available = g.Sum(x => x.RemainingQuantity - x.ReservedQuantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Available, ct);

        foreach (var group in request.Items.GroupBy(i => i.ProductId))
        {
            if (!products.TryGetValue(group.Key, out var product) || product.IsArchived)
            {
                throw new DomainValidationException($"Product ({group.Key}) does not exist or is archived.");
            }

            var totalRequested = group.Sum(i => i.RequestedQuantity);
            var available = availableByProduct.GetValueOrDefault(group.Key, 0m);
            if (available < totalRequested)
            {
                throw new InsufficientStockException(product.Name, available, totalRequested, product.BaseUnit.Code);
            }
        }

        var preferredLotIds = request.Items
            .Where(i => i.PreferredFactoryLotId.HasValue)
            .Select(i => i.PreferredFactoryLotId!.Value)
            .Distinct()
            .ToList();

        var preferredLots = preferredLotIds.Count == 0
            ? new Dictionary<long, FactoryStockLot>()
            : await db.FactoryStockLots.AsNoTracking()
                .Where(l => preferredLotIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);

        var delivery = new Delivery
        {
            DeliveryNumber = $"DEL-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Status = DeliveryStatus.Draft,
            CreatedByUserId = userId,
            CreatedAt = clock.UtcNow,
            DriverOrCourierName = request.DriverOrCourierName.Trim(),
            Notes = request.Notes.Trim()
        };

        foreach (var line in request.Items)
        {
            if (line.PreferredFactoryLotId.HasValue)
            {
                if (!preferredLots.TryGetValue(line.PreferredFactoryLotId.Value, out var lot) || lot.ProductId != line.ProductId)
                {
                    throw new DomainValidationException($"Preferred factory lot ({line.PreferredFactoryLotId.Value}) is invalid for product ({line.ProductId}).");
                }

                if (lot.Status != FactoryLotStatus.Available || (lot.ExpirationDate.HasValue && lot.ExpirationDate < today))
                {
                    throw new DomainValidationException($"Factory lot {lot.LotNumber} is not available for dispatch.");
                }

                var lotAvail = lot.RemainingQuantity - lot.ReservedQuantity;
                if (lotAvail < line.RequestedQuantity)
                {
                    throw new InsufficientStockException($"{products[line.ProductId].Name} (Lot {lot.LotNumber})", lotAvail, line.RequestedQuantity, products[line.ProductId].BaseUnit.Code);
                }
            }

            delivery.Items.Add(new DeliveryItem
            {
                ProductId = line.ProductId,
                FactoryStockLotId = line.PreferredFactoryLotId,
                RequestedQuantity = decimal.Round(line.RequestedQuantity, 4, MidpointRounding.AwayFromZero),
                DispatchedQuantity = 0m,
                ReceivedQuantity = 0m,
                DiscrepancyQuantity = 0m,
                ReconciliationStatus = ReconciliationStatus.None
            });
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync(ct);

            await audit.AddAuditEntryAsync(
                "CreateDeliveryDraft",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Created draft delivery {delivery.DeliveryNumber} ({delivery.Items.Count} lines). Store inventory unchanged.",
                newValues: new { delivery.DeliveryNumber, ItemCount = delivery.Items.Count },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(delivery.Id, ct);
    }

    public async Task<DeliveryDto> PrepareForDispatchAsync(long deliveryId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var delivery = await db.Deliveries
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
                ?? throw new EntityNotFoundException(nameof(Delivery), deliveryId);

            if (delivery.Status != DeliveryStatus.Draft)
            {
                throw new DomainValidationException($"Only Draft deliveries can be prepared. Current status: {delivery.Status}.");
            }

            await AllocateAndReserveLotsAsync(delivery, ct);
            delivery.Status = DeliveryStatus.PendingDispatch;

            await audit.AddAuditEntryAsync(
                "PrepareDelivery",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Prepared delivery {delivery.DeliveryNumber} and reserved factory stock lots.",
                oldValues: new { Status = DeliveryStatus.Draft },
                newValues: new { Status = DeliveryStatus.PendingDispatch, LotLines = delivery.Items.Count },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(deliveryId, ct);
    }

    public async Task<DeliveryDto> DispatchAsync(long deliveryId, DispatchDeliveryRequest? request = null, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var delivery = await db.Deliveries
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
                ?? throw new EntityNotFoundException(nameof(Delivery), deliveryId);

            if (delivery.Status is DeliveryStatus.Dispatched or DeliveryStatus.PartiallyReceived or DeliveryStatus.Received)
            {
                throw new DuplicateTransactionException(delivery.DeliveryNumber);
            }

            if (delivery.Status == DeliveryStatus.Cancelled)
            {
                throw new DomainValidationException($"Cancelled delivery {delivery.DeliveryNumber} cannot be dispatched.");
            }

            if (delivery.Status == DeliveryStatus.Draft)
            {
                await AllocateAndReserveLotsAsync(delivery, ct);
                await db.SaveChangesAsync(ct);
            }

            if (!string.IsNullOrWhiteSpace(request?.DriverOrCourierName))
            {
                delivery.DriverOrCourierName = request.DriverOrCourierName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request?.Notes))
            {
                delivery.Notes = string.IsNullOrWhiteSpace(delivery.Notes)
                    ? request.Notes.Trim()
                    : $"{delivery.Notes} | {request.Notes.Trim()}";
            }

            var overrideMap = request?.ItemQuantities?.ToDictionary(x => x.DeliveryItemId, x => x.DispatchedQuantity)
                ?? new Dictionary<long, decimal>();

            var lotIds = delivery.Items
                .Where(i => i.FactoryStockLotId.HasValue)
                .Select(i => i.FactoryStockLotId!.Value)
                .Distinct()
                .ToList();

            var lots = await db.FactoryStockLots
                .Where(l => lotIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, ct);

            foreach (var item in delivery.Items)
            {
                if (!item.FactoryStockLotId.HasValue || !lots.TryGetValue(item.FactoryStockLotId.Value, out var lot))
                {
                    throw new DomainValidationException($"Delivery item ({item.Id}) is missing an allocated factory stock lot.");
                }

                var dispatchQty = overrideMap.TryGetValue(item.Id, out var customQty)
                    ? decimal.Round(customQty, 4, MidpointRounding.AwayFromZero)
                    : item.RequestedQuantity;

                if (dispatchQty <= 0 || dispatchQty > item.RequestedQuantity)
                {
                    throw new DomainValidationException(
                        $"Dispatched quantity ({dispatchQty:0.####}) for item #{item.Id} must be > 0 and <= requested ({item.RequestedQuantity:0.####}).");
                }

                lot.ReservedQuantity = Math.Max(0m, decimal.Round(lot.ReservedQuantity - item.RequestedQuantity, 4, MidpointRounding.AwayFromZero));
                item.DispatchedQuantity = dispatchQty;

                await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                    FactoryStockLotId: lot.Id,
                    QuantityDelta: -dispatchQty,
                    SourceLocation: InventoryLocation.Factory,
                    DestinationLocation: InventoryLocation.InTransit,
                    MovementType: InventoryMovementType.FactoryDispatch,
                    ReferenceType: nameof(Delivery),
                    ReferenceId: delivery.Id.ToString(),
                    ReferenceNumber: delivery.DeliveryNumber,
                    Remarks: $"Dispatched to store under {delivery.DeliveryNumber}.",
                    DeduplicationKey: $"FactoryDispatch:Delivery:{delivery.Id}:Item:{item.Id}:LOT:{lot.Id}"), ct);
            }

            delivery.Status = DeliveryStatus.Dispatched;
            delivery.DispatchedAt = clock.UtcNow;
            delivery.DispatchedByUserId = userId;

            await audit.AddAuditEntryAsync(
                "DispatchDelivery",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Dispatched delivery {delivery.DeliveryNumber} ({delivery.Items.Sum(i => i.DispatchedQuantity):0.####} total units).",
                oldValues: new { Status = DeliveryStatus.PendingDispatch },
                newValues: new { Status = DeliveryStatus.Dispatched, delivery.DispatchedAt },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(deliveryId, ct);
    }

    public async Task CancelAsync(long deliveryId, string reason, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException("A cancellation reason is required.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var delivery = await db.Deliveries
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
                ?? throw new EntityNotFoundException(nameof(Delivery), deliveryId);

            if (delivery.Status is not (DeliveryStatus.Draft or DeliveryStatus.PendingDispatch))
            {
                throw new DomainValidationException($"Only Draft or PendingDispatch deliveries can be cancelled. Current status: {delivery.Status}.");
            }

            var prevStatus = delivery.Status;
            if (prevStatus == DeliveryStatus.PendingDispatch)
            {
                var lotIds = delivery.Items
                    .Where(i => i.FactoryStockLotId.HasValue)
                    .Select(i => i.FactoryStockLotId!.Value)
                    .Distinct()
                    .ToList();

                var lots = await db.FactoryStockLots.Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
                foreach (var item in delivery.Items)
                {
                    if (item.FactoryStockLotId.HasValue && lots.TryGetValue(item.FactoryStockLotId.Value, out var lot))
                    {
                        lot.ReservedQuantity = Math.Max(0m, decimal.Round(lot.ReservedQuantity - item.RequestedQuantity, 4, MidpointRounding.AwayFromZero));
                    }
                }
            }

            delivery.Status = DeliveryStatus.Cancelled;
            delivery.Notes = string.IsNullOrWhiteSpace(delivery.Notes)
                ? $"Cancelled: {reason.Trim()}"
                : $"{delivery.Notes} | Cancelled: {reason.Trim()}";

            await audit.AddAuditEntryAsync(
                "CancelDelivery",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Cancelled delivery {delivery.DeliveryNumber}. Reason: {reason.Trim()}",
                oldValues: new { Status = prevStatus },
                newValues: new { Status = DeliveryStatus.Cancelled },
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

    public async Task<DeliveryDto> ConfirmReceivingAsync(long deliveryId, ConfirmDeliveryReceivingRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(receiveValidator, request, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var delivery = await db.Deliveries
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
                ?? throw new EntityNotFoundException(nameof(Delivery), deliveryId);

            if (delivery.Status is DeliveryStatus.Received or DeliveryStatus.PartiallyReceived)
            {
                throw new DuplicateTransactionException(delivery.DeliveryNumber);
            }

            if (delivery.Status != DeliveryStatus.Dispatched)
            {
                throw new DomainValidationException($"Only Dispatched deliveries can be received by the store. Current status: {delivery.Status}.");
            }

            if (request.Items.Select(i => i.DeliveryItemId).Distinct().Count() != request.Items.Count)
            {
                throw new DomainValidationException("Duplicate delivery item entries in receiving confirmation.");
            }

            var inputMap = request.Items.ToDictionary(i => i.DeliveryItemId);
            if (delivery.Items.Any(i => !inputMap.ContainsKey(i.Id)))
            {
                throw new DomainValidationException("All dispatched delivery items must be verified during receiving confirmation.");
            }

            bool hasAnyDiscrepancy = false;

            foreach (var item in delivery.Items)
            {
                var input = inputMap[item.Id];
                var receivedQty = decimal.Round(input.ReceivedQuantity, 4, MidpointRounding.AwayFromZero);
                var discrepancy = decimal.Round(item.DispatchedQuantity - receivedQty, 4, MidpointRounding.AwayFromZero);

                if (discrepancy != 0m && string.IsNullOrWhiteSpace(input.DiscrepancyReason))
                {
                    throw new DomainValidationException(
                        $"A discrepancy reason is required for item #{item.Id} (Dispatched: {item.DispatchedQuantity:0.####}, Received: {receivedQty:0.####}).");
                }

                item.ReceivedQuantity = receivedQty;
                item.DiscrepancyQuantity = discrepancy;
                item.DiscrepancyReason = input.DiscrepancyReason?.Trim() ?? string.Empty;
                item.ReconciliationStatus = discrepancy == 0m ? ReconciliationStatus.None : ReconciliationStatus.PendingReview;

                if (discrepancy != 0m)
                {
                    hasAnyDiscrepancy = true;
                }

                if (receivedQty > 0m)
                {
                    await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                        ProductId: item.ProductId,
                        QuantityDelta: receivedQty,
                        SourceLocation: InventoryLocation.InTransit,
                        DestinationLocation: InventoryLocation.Store,
                        MovementType: InventoryMovementType.StoreReceiving,
                        ReferenceType: nameof(Delivery),
                        ReferenceId: delivery.Id.ToString(),
                        ReferenceNumber: delivery.DeliveryNumber,
                        Remarks: $"Store receiving confirmed under {delivery.DeliveryNumber}.",
                        DeduplicationKey: $"StoreReceiving:Delivery:{delivery.Id}:Item:{item.Id}:PROD:{item.ProductId}",
                        UpdateLastReceivedAt: true), ct);
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Notes))
            {
                delivery.Notes = string.IsNullOrWhiteSpace(delivery.Notes)
                    ? request.Notes.Trim()
                    : $"{delivery.Notes} | Store Receiving: {request.Notes.Trim()}";
            }

            delivery.ReceivedAt = clock.UtcNow;
            delivery.ReceivedByUserId = userId;
            delivery.Status = hasAnyDiscrepancy ? DeliveryStatus.PartiallyReceived : DeliveryStatus.Received;

            await audit.AddAuditEntryAsync(
                "ReceiveDelivery",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Store confirmed receiving for {delivery.DeliveryNumber} (Status: {delivery.Status}, Received total: {delivery.Items.Sum(i => i.ReceivedQuantity):0.####}).",
                oldValues: new { Status = DeliveryStatus.Dispatched },
                newValues: new { delivery.Status, delivery.ReceivedAt, HasDiscrepancy = hasAnyDiscrepancy },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(deliveryId, ct);
    }

    public async Task<DeliveryDto> ReconcileDiscrepancyAsync(long deliveryId, ReconcileDeliveryRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(reconcileValidator, request, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var delivery = await db.Deliveries
                .Include(d => d.Items)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
                ?? throw new EntityNotFoundException(nameof(Delivery), deliveryId);

            if (delivery.Status != DeliveryStatus.PartiallyReceived)
            {
                throw new DomainValidationException($"Only PartiallyReceived deliveries have pending discrepancies to reconcile. Current status: {delivery.Status}.");
            }

            var itemMap = delivery.Items.ToDictionary(i => i.Id);

            foreach (var rec in request.Items)
            {
                if (!itemMap.TryGetValue(rec.DeliveryItemId, out var item))
                {
                    throw new EntityNotFoundException(nameof(DeliveryItem), rec.DeliveryItemId);
                }

                if (item.ReconciliationStatus != ReconciliationStatus.PendingReview)
                {
                    throw new DomainValidationException($"Delivery item #{item.Id} does not have a pending discrepancy.");
                }

                switch (rec.ResolutionType)
                {
                    case DiscrepancyResolutionType.ReturnToFactoryLot:
                        if (item.DiscrepancyQuantity <= 0)
                        {
                            throw new DomainValidationException("ReturnToFactoryLot applies only to delivery shortages (Dispatched > Received).");
                        }
                        if (!item.FactoryStockLotId.HasValue)
                        {
                            throw new DomainValidationException($"Delivery item #{item.Id} has no linked factory stock lot.");
                        }

                        await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                            FactoryStockLotId: item.FactoryStockLotId.Value,
                            QuantityDelta: item.DiscrepancyQuantity,
                            SourceLocation: InventoryLocation.InTransit,
                            DestinationLocation: InventoryLocation.Factory,
                            MovementType: InventoryMovementType.StockAdjustment,
                            ReferenceType: nameof(Delivery),
                            ReferenceId: delivery.Id.ToString(),
                            ReferenceNumber: delivery.DeliveryNumber,
                            Remarks: $"Delivery shortage returned to factory: {rec.ResolutionRemarks.Trim()}",
                            DeduplicationKey: $"DeliveryReturnToFactory:Delivery:{delivery.Id}:Item:{item.Id}:LOT:{item.FactoryStockLotId.Value}"), ct);
                        break;

                    case DiscrepancyResolutionType.TransitLossWriteOff:
                        if (item.DiscrepancyQuantity <= 0)
                        {
                            throw new DomainValidationException("TransitLossWriteOff applies only to delivery shortages (Dispatched > Received).");
                        }

                        db.WasteAndReturnRecords.Add(new WasteAndReturnRecord
                        {
                            ReferenceNumber = $"TRL-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
                            Location = InventoryLocation.InTransit,
                            ProductId = item.ProductId,
                            FactoryStockLotId = item.FactoryStockLotId,
                            Category = WasteReturnCategory.OtherLoss,
                            Quantity = item.DiscrepancyQuantity,
                            Reason = $"Delivery {delivery.DeliveryNumber} transit loss: {rec.ResolutionRemarks.Trim()}",
                            RecordedByUserId = userId,
                            RecordedAt = clock.UtcNow,
                            InspectionOutcome = InspectionOutcome.DisposedAsLoss,
                            ApprovalStatus = ApprovalStatus.Approved,
                            ApprovedByUserId = userId,
                            ApprovedAt = clock.UtcNow
                        });
                        break;

                    case DiscrepancyResolutionType.OverageDeductFromFactory:
                        if (item.DiscrepancyQuantity >= 0)
                        {
                            throw new DomainValidationException("OverageDeductFromFactory applies only when store received more than dispatched.");
                        }
                        if (!item.FactoryStockLotId.HasValue)
                        {
                            throw new DomainValidationException($"Delivery item #{item.Id} has no linked factory stock lot.");
                        }

                        await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                            FactoryStockLotId: item.FactoryStockLotId.Value,
                            QuantityDelta: item.DiscrepancyQuantity, // negative delta
                            SourceLocation: InventoryLocation.Factory,
                            DestinationLocation: InventoryLocation.InTransit,
                            MovementType: InventoryMovementType.FactoryDispatch,
                            ReferenceType: nameof(Delivery),
                            ReferenceId: delivery.Id.ToString(),
                            ReferenceNumber: delivery.DeliveryNumber,
                            Remarks: $"Reconciled delivery overage from factory lot: {rec.ResolutionRemarks.Trim()}",
                            DeduplicationKey: $"DeliveryOverageDeduct:Delivery:{delivery.Id}:Item:{item.Id}:LOT:{item.FactoryStockLotId.Value}"), ct);
                        break;
                }

                item.ReconciliationStatus = ReconciliationStatus.Resolved;
                item.DiscrepancyReason = $"{item.DiscrepancyReason} [Resolved: {rec.ResolutionType} - {rec.ResolutionRemarks.Trim()}]".Trim();
            }

            if (delivery.Items.All(i => i.ReconciliationStatus != ReconciliationStatus.PendingReview))
            {
                delivery.Status = DeliveryStatus.Received;
            }

            await audit.AddAuditEntryAsync(
                "ReconcileDeliveryDiscrepancy",
                nameof(Delivery),
                delivery.Id.ToString(),
                $"Reconciled discrepancies on {delivery.DeliveryNumber}. New status: {delivery.Status}.",
                oldValues: new { Status = DeliveryStatus.PartiallyReceived },
                newValues: new { delivery.Status },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(deliveryId, ct);
    }

    private async Task AllocateAndReserveLotsAsync(Delivery delivery, CancellationToken ct)
    {
        var today = clock.TodayLocal;
        var originalItems = delivery.Items.ToList();
        var productIds = originalItems.Select(i => i.ProductId).Distinct().ToList();

        var products = await db.Products
            .Include(p => p.BaseUnit)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var eligibleLots = await db.FactoryStockLots
            .Where(l => productIds.Contains(l.ProductId)
                        && l.Status == FactoryLotStatus.Available
                        && (l.RemainingQuantity - l.ReservedQuantity) > 0
                        && (l.ExpirationDate == null || l.ExpirationDate >= today))
            .OrderBy(l => l.ExpirationDate == null ? 1 : 0)
            .ThenBy(l => l.ExpirationDate)
            .ThenBy(l => l.ProductionDate)
            .ThenBy(l => l.Id)
            .ToListAsync(ct);

        delivery.Items.Clear();
        db.DeliveryItems.RemoveRange(originalItems);

        foreach (var line in originalItems)
        {
            var product = products[line.ProductId];
            if (line.FactoryStockLotId.HasValue)
            {
                var lot = eligibleLots.FirstOrDefault(l => l.Id == line.FactoryStockLotId.Value)
                    ?? throw new DomainValidationException($"Specified lot #{line.FactoryStockLotId.Value} is no longer available.");

                var available = lot.RemainingQuantity - lot.ReservedQuantity;
                if (available < line.RequestedQuantity)
                {
                    throw new InsufficientStockException($"{product.Name} (Lot {lot.LotNumber})", available, line.RequestedQuantity, product.BaseUnit.Code);
                }

                lot.ReservedQuantity = decimal.Round(lot.ReservedQuantity + line.RequestedQuantity, 4, MidpointRounding.AwayFromZero);
                delivery.Items.Add(new DeliveryItem
                {
                    ProductId = line.ProductId,
                    FactoryStockLotId = lot.Id,
                    RequestedQuantity = line.RequestedQuantity,
                    ReconciliationStatus = ReconciliationStatus.None
                });
                continue;
            }

            var remainingToAllocate = line.RequestedQuantity;
            foreach (var lot in eligibleLots.Where(l => l.ProductId == line.ProductId))
            {
                if (remainingToAllocate <= 0m) break;

                var available = lot.RemainingQuantity - lot.ReservedQuantity;
                if (available <= 0m) continue;

                var take = Math.Min(available, remainingToAllocate);
                lot.ReservedQuantity = decimal.Round(lot.ReservedQuantity + take, 4, MidpointRounding.AwayFromZero);
                remainingToAllocate = decimal.Round(remainingToAllocate - take, 4, MidpointRounding.AwayFromZero);

                delivery.Items.Add(new DeliveryItem
                {
                    ProductId = line.ProductId,
                    FactoryStockLotId = lot.Id,
                    RequestedQuantity = take,
                    ReconciliationStatus = ReconciliationStatus.None
                });
            }

            if (remainingToAllocate > 0m)
            {
                var totalAvail = eligibleLots.Where(l => l.ProductId == line.ProductId).Sum(l => l.RemainingQuantity - l.ReservedQuantity);
                throw new InsufficientStockException(product.Name, totalAvail, line.RequestedQuantity, product.BaseUnit.Code);
            }
        }
    }

    private static IQueryable<DeliveryDto> ProjectDeliveries(IQueryable<Delivery> source) =>
        source.Select(d => new DeliveryDto(
            d.Id,
            d.DeliveryNumber,
            d.Status,
            d.CreatedByUser.FullName,
            d.DispatchedByUser != null ? d.DispatchedByUser.FullName : null,
            d.ReceivedByUser != null ? d.ReceivedByUser.FullName : null,
            d.CreatedAt,
            d.DispatchedAt,
            d.ReceivedAt,
            d.DriverOrCourierName,
            d.Notes,
            d.Items.Select(i => new DeliveryItemDto(
                i.Id,
                i.ProductId,
                i.Product.Sku,
                i.Product.Name,
                i.Product.BaseUnit.Code,
                i.FactoryStockLotId,
                i.FactoryStockLot != null ? i.FactoryStockLot.LotNumber : null,
                i.RequestedQuantity,
                i.DispatchedQuantity,
                i.ReceivedQuantity,
                i.DiscrepancyQuantity,
                i.DiscrepancyReason,
                i.ReconciliationStatus)).ToList()));
}
