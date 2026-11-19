using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IWasteAndReturnService
{
    Task<PagedResult<WasteAndReturnDto>> GetPagedAsync(WasteAndReturnQuery query, CancellationToken ct = default);
    Task<WasteAndReturnDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<WasteAndReturnDto> RecordAsync(RecordWasteOrReturnRequest request, CancellationToken ct = default);
    Task<WasteAndReturnDto> InspectCustomerReturnAsync(long recordId, InspectCustomerReturnRequest request, CancellationToken ct = default);
}

public sealed class WasteAndReturnService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<RecordWasteOrReturnRequest> recordValidator,
    IValidator<InspectCustomerReturnRequest> inspectValidator) : IWasteAndReturnService
{
    public async Task<PagedResult<WasteAndReturnDto>> GetPagedAsync(WasteAndReturnQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.WasteAndReturnRecords.AsNoTracking();

        if (currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            q = q.Where(x => x.Location == InventoryLocation.Store);
        }
        else if (currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            q = q.Where(x => x.Location == InventoryLocation.Factory);
        }

        if (query.Category.HasValue)
        {
            q = q.Where(x => x.Category == query.Category.Value);
        }

        if (query.Outcome.HasValue)
        {
            q = q.Where(x => x.InspectionOutcome == query.Outcome.Value);
        }

        if (query.Location.HasValue)
        {
            q = q.Where(x => x.Location == query.Location.Value);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(x => x.RecordedAt >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(x => x.RecordedAt <= query.ToUtc.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await ProjectRecords(
            q.OrderByDescending(x => x.RecordedAt)
                .ThenByDescending(x => x.Id)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<WasteAndReturnDto>(items, total, page, size);
    }

    public async Task<WasteAndReturnDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);

        return await ProjectRecords(db.WasteAndReturnRecords.AsNoTracking().Where(x => x.Id == id))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(WasteAndReturnRecord), id);
    }

    public async Task<WasteAndReturnDto> RecordAsync(RecordWasteOrReturnRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker, AppRoles.Cashier);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(recordValidator, request, ct);

        if (currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            && request.Location != InventoryLocation.Store)
        {
            throw new UnauthorizedDomainActionException("Cashiers can only record returns or losses for Store inventory.");
        }

        if (currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            && request.Location != InventoryLocation.Factory)
        {
            throw new UnauthorizedDomainActionException("Factory staff can only record losses for Factory inventory.");
        }

        if (request.Category == WasteReturnCategory.CustomerReturn && request.Location != InventoryLocation.Store)
        {
            throw new DomainValidationException("Customer returns apply only to Store transactions.");
        }

        var qty = decimal.Round(request.Quantity, 4, MidpointRounding.AwayFromZero);
        var isCustomerReturn = request.Category == WasteReturnCategory.CustomerReturn;

        var record = new WasteAndReturnRecord
        {
            ReferenceNumber = $"{(isCustomerReturn ? "RET" : "WST")}-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Location = request.Location,
            ProductId = request.ProductId,
            RawMaterialId = request.RawMaterialId,
            FactoryStockLotId = request.FactoryStockLotId,
            PosTransactionId = request.PosTransactionId,
            Category = request.Category,
            Quantity = qty,
            Reason = request.Reason.Trim(),
            RecordedByUserId = userId,
            RecordedAt = clock.UtcNow,
            InspectionOutcome = isCustomerReturn ? InspectionOutcome.PendingInspection : InspectionOutcome.DisposedAsLoss,
            ApprovalStatus = isCustomerReturn ? ApprovalStatus.Pending : ApprovalStatus.Approved,
            ApprovedByUserId = isCustomerReturn ? null : userId,
            ApprovedAt = isCustomerReturn ? null : clock.UtcNow
        };

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            if (request.Location == InventoryLocation.Store)
            {
                var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId!.Value, ct)
                    ?? throw new EntityNotFoundException(nameof(Product), request.ProductId!.Value);
                record.ProductId = product.Id;
            }
            else if (request.FactoryStockLotId.HasValue)
            {
                var lot = await db.FactoryStockLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.FactoryStockLotId.Value, ct)
                    ?? throw new EntityNotFoundException(nameof(FactoryStockLot), request.FactoryStockLotId.Value);
                record.ProductId = lot.ProductId;
            }
            else if (request.RawMaterialId.HasValue)
            {
                var mat = await db.RawMaterials.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.RawMaterialId.Value, ct)
                    ?? throw new EntityNotFoundException(nameof(RawMaterial), request.RawMaterialId.Value);
                record.RawMaterialId = mat.Id;
            }

            db.WasteAndReturnRecords.Add(record);
            await db.SaveChangesAsync(ct);

            // Damaged, spoiled, expired, and other losses are immediately removed from sellable stock.
            if (!isCustomerReturn)
            {
                var movementType = request.Category switch
                {
                    WasteReturnCategory.Damaged => InventoryMovementType.DamagedProduct,
                    WasteReturnCategory.Spoiled => InventoryMovementType.SpoiledProduct,
                    WasteReturnCategory.Expired => InventoryMovementType.ExpiredProduct,
                    _ => InventoryMovementType.DamagedProduct
                };

                if (request.Location == InventoryLocation.Store)
                {
                    await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                        ProductId: record.ProductId!.Value,
                        QuantityDelta: -qty,
                        SourceLocation: InventoryLocation.Store,
                        DestinationLocation: InventoryLocation.WasteDisposal,
                        MovementType: movementType,
                        ReferenceType: nameof(WasteAndReturnRecord),
                        ReferenceId: record.Id.ToString(),
                        ReferenceNumber: record.ReferenceNumber,
                        Remarks: $"{request.Category}: {record.Reason}",
                        DeduplicationKey: $"WasteStore:{record.Id}"), ct);
                }
                else if (request.FactoryStockLotId.HasValue)
                {
                    await ledger.RecordFactoryLotMovementAsync(new RecordFactoryLotMovementRequest(
                        FactoryStockLotId: request.FactoryStockLotId.Value,
                        QuantityDelta: -qty,
                        SourceLocation: InventoryLocation.Factory,
                        DestinationLocation: InventoryLocation.WasteDisposal,
                        MovementType: movementType,
                        ReferenceType: nameof(WasteAndReturnRecord),
                        ReferenceId: record.Id.ToString(),
                        ReferenceNumber: record.ReferenceNumber,
                        Remarks: $"{request.Category}: {record.Reason}",
                        DeduplicationKey: $"WasteFactoryLot:{record.Id}"), ct);
                }
                else if (request.RawMaterialId.HasValue)
                {
                    var mat = await db.RawMaterials.AsNoTracking().FirstAsync(m => m.Id == request.RawMaterialId.Value, ct);
                    await ledger.RecordRawMaterialMovementAsync(new RecordRawMaterialMovementRequest(
                        RawMaterialId: mat.Id,
                        QuantityDelta: -qty,
                        UnitId: mat.UnitId,
                        SourceLocation: InventoryLocation.Factory,
                        DestinationLocation: InventoryLocation.WasteDisposal,
                        MovementType: movementType,
                        ReferenceType: nameof(WasteAndReturnRecord),
                        ReferenceId: record.Id.ToString(),
                        ReferenceNumber: record.ReferenceNumber,
                        Remarks: $"{request.Category}: {record.Reason}",
                        DeduplicationKey: $"WasteRawMaterial:{record.Id}"), ct);
                }
            }

            await audit.AddAuditEntryAsync(
                isCustomerReturn ? "RecordCustomerReturn" : "RecordInventoryLoss",
                nameof(WasteAndReturnRecord),
                record.Id.ToString(),
                $"Recorded {request.Category} ({record.ReferenceNumber}, Qty: {qty:0.####}, Location: {request.Location}). Reason: {record.Reason}",
                newValues: new { record.ReferenceNumber, record.Category, record.Quantity, record.InspectionOutcome },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(record.Id, ct);
    }

    public async Task<WasteAndReturnDto> InspectCustomerReturnAsync(
        long recordId,
        InspectCustomerReturnRequest request,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var actorId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(inspectValidator, request, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var record = await db.WasteAndReturnRecords
                .FirstOrDefaultAsync(x => x.Id == recordId, ct)
                ?? throw new EntityNotFoundException(nameof(WasteAndReturnRecord), recordId);

            if (record.Category != WasteReturnCategory.CustomerReturn)
            {
                throw new DomainValidationException("Only CustomerReturn records require inspection.");
            }

            if (record.InspectionOutcome != InspectionOutcome.PendingInspection)
            {
                throw new DuplicateTransactionException(record.ReferenceNumber);
            }

            record.InspectionOutcome = request.Outcome;
            record.ApprovalStatus = ApprovalStatus.Approved;
            record.ApprovedByUserId = actorId;
            record.ApprovedAt = clock.UtcNow;
            record.Reason = $"{record.Reason} | Inspection ({request.Outcome}): {request.InspectionNotes.Trim()}";

            if (request.Outcome == InspectionOutcome.RestockedToSellable)
            {
                if (!record.ProductId.HasValue)
                {
                    throw new DomainValidationException("Customer return record is missing a ProductId.");
                }

                await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                    ProductId: record.ProductId.Value,
                    QuantityDelta: record.Quantity,
                    SourceLocation: InventoryLocation.ExternalCustomer,
                    DestinationLocation: InventoryLocation.Store,
                    MovementType: InventoryMovementType.CustomerReturn,
                    ReferenceType: nameof(WasteAndReturnRecord),
                    ReferenceId: record.Id.ToString(),
                    ReferenceNumber: record.ReferenceNumber,
                    Remarks: $"Approved restock after inspection: {request.InspectionNotes.Trim()}",
                    DeduplicationKey: $"CustomerReturnRestock:{record.Id}"), ct);
            }

            await audit.AddAuditEntryAsync(
                "InspectCustomerReturn",
                nameof(WasteAndReturnRecord),
                record.Id.ToString(),
                $"Inspected customer return {record.ReferenceNumber}: {request.Outcome} (Qty: {record.Quantity:0.####}).",
                oldValues: new { InspectionOutcome = InspectionOutcome.PendingInspection },
                newValues: new { record.InspectionOutcome, record.ApprovedAt },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(recordId, ct);
    }

    private static IQueryable<WasteAndReturnDto> ProjectRecords(IQueryable<WasteAndReturnRecord> source) =>
        source.Select(x => new WasteAndReturnDto(
            x.Id,
            x.ReferenceNumber,
            x.Location,
            x.ProductId,
            x.Product != null ? x.Product.Name : null,
            x.RawMaterialId,
            x.RawMaterial != null ? x.RawMaterial.Name : null,
            x.FactoryStockLotId,
            x.FactoryStockLot != null ? x.FactoryStockLot.LotNumber : null,
            x.PosTransactionId,
            x.PosTransaction != null ? x.PosTransaction.ReceiptNumber : null,
            x.Category,
            x.Quantity,
            x.Reason,
            x.RecordedByUser.FullName,
            x.RecordedAt,
            x.InspectionOutcome,
            x.ApprovalStatus,
            x.ApprovedByUser != null ? x.ApprovedByUser.FullName : null,
            x.ApprovedAt));
}
