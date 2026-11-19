using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IMaterialReceiptService
{
    Task<PagedResult<MaterialReceiptDto>> GetPagedAsync(MaterialReceiptQuery query, CancellationToken ct = default);
    Task<MaterialReceiptDto> GetByIdAsync(long id, CancellationToken ct = default);
    Task<MaterialReceiptDto> CreateDraftAsync(CreateMaterialReceiptRequest request, CancellationToken ct = default);
    Task<MaterialReceiptDto> ConfirmAsync(long receiptId, CancellationToken ct = default);
    Task CancelDraftAsync(long receiptId, string reason, CancellationToken ct = default);
}

public sealed class MaterialReceiptService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    IDateTimeProvider clock,
    IValidator<CreateMaterialReceiptRequest> validator) : IMaterialReceiptService
{
    public async Task<PagedResult<MaterialReceiptDto>> GetPagedAsync(MaterialReceiptQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.MaterialReceipts.AsNoTracking();

        if (query.SupplierId.HasValue)
        {
            q = q.Where(x => x.SupplierId == query.SupplierId.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(x => x.Status == query.Status.Value);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(x => x.ReceivingDate >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(x => x.ReceivingDate <= query.ToUtc.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.ReceivingDate)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new MaterialReceiptDto(
                x.Id,
                x.ReceiptNumber,
                x.SupplierId,
                x.Supplier.Name,
                x.ReceivingDate,
                x.ReceivedByUserId,
                x.ReceivedByUser.FullName,
                x.Status,
                x.TotalCost,
                x.Remarks,
                x.ConfirmedAt,
                x.ConfirmedByUser != null ? x.ConfirmedByUser.FullName : null,
                x.Items.Select(i => new MaterialReceiptItemDto(
                    i.Id,
                    i.RawMaterialId,
                    i.RawMaterial.Sku,
                    i.RawMaterial.Name,
                    i.Quantity,
                    i.UnitId,
                    i.Unit.Code,
                    i.ConvertedBaseQuantity,
                    i.RawMaterial.Unit.Code,
                    i.UnitCost,
                    i.LineTotal)).ToList()))
            .ToListAsync(ct);

        return new PagedResult<MaterialReceiptDto>(items, total, page, size);
    }

    public async Task<MaterialReceiptDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        return await db.MaterialReceipts.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new MaterialReceiptDto(
                x.Id,
                x.ReceiptNumber,
                x.SupplierId,
                x.Supplier.Name,
                x.ReceivingDate,
                x.ReceivedByUserId,
                x.ReceivedByUser.FullName,
                x.Status,
                x.TotalCost,
                x.Remarks,
                x.ConfirmedAt,
                x.ConfirmedByUser != null ? x.ConfirmedByUser.FullName : null,
                x.Items.Select(i => new MaterialReceiptItemDto(
                    i.Id,
                    i.RawMaterialId,
                    i.RawMaterial.Sku,
                    i.RawMaterial.Name,
                    i.Quantity,
                    i.UnitId,
                    i.Unit.Code,
                    i.ConvertedBaseQuantity,
                    i.RawMaterial.Unit.Code,
                    i.UnitCost,
                    i.LineTotal)).ToList()))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(MaterialReceipt), id);
    }

    public async Task<MaterialReceiptDto> CreateDraftAsync(CreateMaterialReceiptRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(validator, request, ct);

        var supplier = await db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.SupplierId, ct)
            ?? throw new EntityNotFoundException(nameof(Supplier), request.SupplierId);

        if (supplier.Status != SupplierStatus.Active)
        {
            throw new DomainValidationException($"Supplier '{supplier.Name}' is inactive and cannot accept new receiving records.");
        }

        var materialIds = request.Items.Select(i => i.RawMaterialId).Distinct().ToList();
        var unitIds = request.Items.Select(i => i.UnitId).Distinct().ToList();

        var materials = await db.RawMaterials
            .Include(m => m.Unit)
            .Where(m => materialIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var units = await db.UnitsOfMeasure
            .Where(u => unitIds.Contains(u.Id) && u.IsActive)
            .ToDictionaryAsync(u => u.Id, ct);

        var receipt = new MaterialReceipt
        {
            ReceiptNumber = $"RCV-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            SupplierId = supplier.Id,
            ReceivingDate = request.ReceivingDate,
            ReceivedByUserId = userId,
            Status = MaterialReceiptStatus.Draft,
            Remarks = request.Remarks.Trim()
        };

        decimal totalCost = 0m;
        foreach (var itemInput in request.Items)
        {
            if (!materials.TryGetValue(itemInput.RawMaterialId, out var material) || material.IsArchived)
            {
                throw new DomainValidationException($"Raw material ({itemInput.RawMaterialId}) does not exist or is archived.");
            }

            if (!units.TryGetValue(itemInput.UnitId, out var inputUnit))
            {
                throw new DomainValidationException($"Unit of measure ({itemInput.UnitId}) does not exist or is inactive.");
            }

            var convertedQty = UnitConversionHelper.Convert(
                itemInput.Quantity,
                inputUnit,
                material.Unit,
                material.PackToBaseRatio);

            var lineTotal = itemInput.UnitCost.HasValue
                ? decimal.Round(itemInput.Quantity * itemInput.UnitCost.Value, 2, MidpointRounding.AwayFromZero)
                : 0m;

            totalCost += lineTotal;

            receipt.Items.Add(new MaterialReceiptItem
            {
                RawMaterialId = material.Id,
                Quantity = decimal.Round(itemInput.Quantity, 4, MidpointRounding.AwayFromZero),
                UnitId = inputUnit.Id,
                ConvertedBaseQuantity = convertedQty,
                UnitCost = itemInput.UnitCost.HasValue ? decimal.Round(itemInput.UnitCost.Value, 2, MidpointRounding.AwayFromZero) : null,
                LineTotal = lineTotal
            });
        }

        receipt.TotalCost = decimal.Round(totalCost, 2, MidpointRounding.AwayFromZero);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            db.MaterialReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);

            await audit.AddAuditEntryAsync(
                "CreateMaterialReceiptDraft",
                nameof(MaterialReceipt),
                receipt.Id.ToString(),
                $"Created draft receiving {receipt.ReceiptNumber} from '{supplier.Name}' ({receipt.Items.Count} lines). Inventory unchanged pending confirmation.",
                newValues: new { receipt.ReceiptNumber, receipt.SupplierId, receipt.TotalCost },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(receipt.Id, ct);
    }

    public async Task<MaterialReceiptDto> ConfirmAsync(long receiptId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);
        var userId = currentUser.EnsureAuthenticatedUserId();

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var receipt = await db.MaterialReceipts
                .Include(r => r.Supplier)
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.Id == receiptId, ct)
                ?? throw new EntityNotFoundException(nameof(MaterialReceipt), receiptId);

            if (receipt.Status == MaterialReceiptStatus.Confirmed)
            {
                throw new DuplicateTransactionException(receipt.ReceiptNumber);
            }

            if (receipt.Status != MaterialReceiptStatus.Draft)
            {
                throw new DomainValidationException($"Only Draft receipts can be confirmed. Current status: {receipt.Status}.");
            }

            if (receipt.Items.Count == 0)
            {
                throw new DomainValidationException("Cannot confirm an empty material receipt.");
            }

            foreach (var item in receipt.Items)
            {
                await ledger.RecordRawMaterialMovementAsync(new RecordRawMaterialMovementRequest(
                    RawMaterialId: item.RawMaterialId,
                    QuantityDelta: item.Quantity,
                    UnitId: item.UnitId,
                    SourceLocation: InventoryLocation.Supplier,
                    DestinationLocation: InventoryLocation.Factory,
                    MovementType: InventoryMovementType.SupplierReceiving,
                    ReferenceType: nameof(MaterialReceipt),
                    ReferenceId: receipt.Id.ToString(),
                    ReferenceNumber: receipt.ReceiptNumber,
                    Remarks: $"Supplier receiving confirmed from '{receipt.Supplier.Name}'.",
                    DeduplicationKey: $"SupplierReceiving:MaterialReceipt:{receipt.Id}:Item:{item.Id}:RM:{item.RawMaterialId}"), ct);
            }

            receipt.Status = MaterialReceiptStatus.Confirmed;
            receipt.ConfirmedAt = clock.UtcNow;
            receipt.ConfirmedByUserId = userId;

            await audit.AddAuditEntryAsync(
                "ConfirmMaterialReceipt",
                nameof(MaterialReceipt),
                receipt.Id.ToString(),
                $"Confirmed material receipt {receipt.ReceiptNumber} from '{receipt.Supplier.Name}' (Total Cost: {receipt.TotalCost:0.00}).",
                oldValues: new { Status = MaterialReceiptStatus.Draft },
                newValues: new { Status = MaterialReceiptStatus.Confirmed, receipt.ConfirmedAt },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetByIdAsync(receiptId, ct);
    }

    public async Task CancelDraftAsync(long receiptId, string reason, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Baker);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException("A cancellation reason is required.");
        }

        var receipt = await db.MaterialReceipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct)
            ?? throw new EntityNotFoundException(nameof(MaterialReceipt), receiptId);

        if (receipt.Status != MaterialReceiptStatus.Draft)
        {
            throw new DomainValidationException("Only Draft material receipts can be cancelled.");
        }

        receipt.Status = MaterialReceiptStatus.Cancelled;
        receipt.Remarks = string.IsNullOrWhiteSpace(receipt.Remarks)
            ? $"Cancelled: {reason.Trim()}"
            : $"{receipt.Remarks} | Cancelled: {reason.Trim()}";

        await audit.AddAuditEntryAsync(
            "CancelMaterialReceiptDraft",
            nameof(MaterialReceipt),
            receipt.Id.ToString(),
            $"Cancelled draft receipt {receipt.ReceiptNumber}. Reason: {reason.Trim()}",
            oldValues: new { Status = MaterialReceiptStatus.Draft },
            newValues: new { Status = MaterialReceiptStatus.Cancelled },
            ct: ct);

        await db.SaveChangesAsync(ct);
    }
}
