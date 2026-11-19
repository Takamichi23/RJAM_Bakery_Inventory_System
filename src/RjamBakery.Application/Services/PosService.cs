using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Validators;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IPosService
{
    Task<IReadOnlyList<PosCatalogItemDto>> GetPosCatalogAsync(string? searchTerm = null, int? categoryId = null, CancellationToken ct = default);
    Task<PosReceiptDto> CompleteSaleAsync(CompletePosSaleRequest request, CancellationToken ct = default);
    Task<PosReceiptDto> GetReceiptAsync(long transactionId, CancellationToken ct = default);
    Task<PagedResult<PosTransactionDto>> GetTransactionsPagedAsync(PosTransactionQuery query, CancellationToken ct = default);
    Task<PosTransactionDto> ReverseTransactionAsync(long transactionId, ReversePosTransactionRequest request, CancellationToken ct = default);
}

public sealed class PosService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IInventoryLedgerService ledger,
    IAuditLogService audit,
    ISystemSettingService settingService,
    IDateTimeProvider clock,
    IValidator<CompletePosSaleRequest> saleValidator,
    IValidator<ReversePosTransactionRequest> reverseValidator) : IPosService
{
    public async Task<IReadOnlyList<PosCatalogItemDto>> GetPosCatalogAsync(
        string? searchTerm = null,
        int? categoryId = null,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Cashier);

        var q = db.Products.AsNoTracking()
            .Where(p => !p.IsArchived && p.Category.IsActive);

        if (categoryId.HasValue)
        {
            q = q.Where(p => p.CategoryId == categoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            q = q.Where(p => p.Name.Contains(term) || p.Sku.Contains(term));
        }

        return await q
            .OrderBy(p => p.Category.DisplayOrder)
            .ThenBy(p => p.Name)
            .Select(p => new PosCatalogItemDto(
                p.Id,
                p.Sku,
                p.Name,
                p.Description,
                p.CategoryId,
                p.Category.Name,
                p.BaseUnit.Code,
                p.SellingPrice,
                p.ImagePath,
                p.StoreInventory != null ? p.StoreInventory.CurrentQuantity : 0m,
                p.StoreInventory != null ? p.StoreInventory.MinStockLevel : p.MinStockLevel))
            .ToListAsync(ct);
    }

    public async Task<PosReceiptDto> CompleteSaleAsync(CompletePosSaleRequest request, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Cashier);
        var userId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(saleValidator, request, ct);

        var idemKey = request.ClientIdempotencyKey.Trim();
        var duplicate = await db.PosTransactions.AsNoTracking()
            .AnyAsync(t => t.ClientIdempotencyKey == idemKey, ct);
        if (duplicate)
        {
            throw new DuplicateTransactionException(idemKey);
        }

        var productIds = request.Items.Select(i => i.ProductId).ToList();

        long savedTxId;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var products = await db.Products
                .Include(p => p.BaseUnit)
                .Include(p => p.StoreInventory)
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            decimal subTotal = 0m;
            var lines = new List<(Product Product, decimal Qty, decimal LineTotal)>();

            foreach (var itemInput in request.Items)
            {
                if (!products.TryGetValue(itemInput.ProductId, out var product) || product.IsArchived)
                {
                    throw new DomainValidationException($"Product ({itemInput.ProductId}) does not exist or is archived.");
                }

                var available = product.StoreInventory?.CurrentQuantity ?? 0m;
                var qty = decimal.Round(itemInput.Quantity, 4, MidpointRounding.AwayFromZero);
                if (available < qty)
                {
                    throw new InsufficientStockException(product.Name, available, qty, product.BaseUnit.Code);
                }

                var lineTotal = decimal.Round(product.SellingPrice * qty, 2, MidpointRounding.AwayFromZero);
                subTotal += lineTotal;
                lines.Add((product, qty, lineTotal));
            }

            subTotal = decimal.Round(subTotal, 2, MidpointRounding.AwayFromZero);
            var discount = decimal.Round(request.DiscountAmount, 2, MidpointRounding.AwayFromZero);
            if (discount > subTotal)
            {
                throw new DomainValidationException($"Discount ({discount:0.00}) cannot exceed subtotal ({subTotal:0.00}).");
            }

            var totalAmount = decimal.Round(subTotal - discount, 2, MidpointRounding.AwayFromZero);
            var tendered = decimal.Round(request.AmountTendered, 2, MidpointRounding.AwayFromZero);
            if (tendered < totalAmount)
            {
                throw new DomainValidationException($"Payment tendered ({tendered:0.00}) is less than total amount ({totalAmount:0.00}).");
            }

            var changeAmount = decimal.Round(tendered - totalAmount, 2, MidpointRounding.AwayFromZero);

            var posTx = new PosTransaction
            {
                ReceiptNumber = $"RCP-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
                ClientIdempotencyKey = idemKey,
                CashierUserId = userId,
                TransactionDate = clock.UtcNow,
                SubTotal = subTotal,
                DiscountAmount = discount,
                TotalAmount = totalAmount,
                PaymentMethod = request.PaymentMethod,
                AmountTendered = tendered,
                ChangeAmount = changeAmount,
                Status = PosTransactionStatus.Completed
            };

            foreach (var (product, qty, lineTotal) in lines)
            {
                posTx.Items.Add(new PosTransactionItem
                {
                    ProductId = product.Id,
                    ProductNameSnapshot = product.Name,
                    SkuSnapshot = product.Sku,
                    UnitPrice = product.SellingPrice,
                    Quantity = qty,
                    LineTotal = lineTotal
                });
            }

            db.PosTransactions.Add(posTx);
            await db.SaveChangesAsync(ct);
            savedTxId = posTx.Id;

            foreach (var item in posTx.Items)
            {
                await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                    ProductId: item.ProductId,
                    QuantityDelta: -item.Quantity,
                    SourceLocation: InventoryLocation.Store,
                    DestinationLocation: InventoryLocation.ExternalCustomer,
                    MovementType: InventoryMovementType.PosSale,
                    ReferenceType: nameof(PosTransaction),
                    ReferenceId: posTx.Id.ToString(),
                    ReferenceNumber: posTx.ReceiptNumber,
                    Remarks: $"POS sale {posTx.ReceiptNumber} ({posTx.PaymentMethod}).",
                    DeduplicationKey: $"PosSale:PosTransaction:{posTx.Id}:PROD:{item.ProductId}",
                    UpdateLastSoldAt: true), ct);
            }

            await audit.AddAuditEntryAsync(
                "PosSaleCompleted",
                nameof(PosTransaction),
                posTx.Id.ToString(),
                $"Completed POS sale {posTx.ReceiptNumber} (Total: {posTx.TotalAmount:0.00}, Method: {posTx.PaymentMethod}, Items: {posTx.Items.Count}).",
                newValues: new { posTx.ReceiptNumber, posTx.TotalAmount, posTx.PaymentMethod, ItemCount = posTx.Items.Count },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(ct);
            throw new DuplicateTransactionException(idemKey);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await GetReceiptAsync(savedTxId, ct);
    }

    public async Task<PosReceiptDto> GetReceiptAsync(long transactionId, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var txDto = await ProjectTransactions(db.PosTransactions.AsNoTracking().Where(t => t.Id == transactionId))
            .FirstOrDefaultAsync(ct)
            ?? throw new EntityNotFoundException(nameof(PosTransaction), transactionId);

        var opConfig = await settingService.GetOperationalConfigAsync(ct);
        return new PosReceiptDto(opConfig.BakeryName, opConfig.CurrencySymbol, txDto);
    }

    public async Task<PagedResult<PosTransactionDto>> GetTransactionsPagedAsync(PosTransactionQuery query, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager, AppRoles.Cashier);

        var page = Math.Max(1, query.PageNumber);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var q = db.PosTransactions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.ReceiptNumber))
        {
            var r = query.ReceiptNumber.Trim();
            q = q.Where(t => t.ReceiptNumber.Contains(r));
        }

        if (query.Status.HasValue)
        {
            q = q.Where(t => t.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.CashierUserId))
        {
            q = q.Where(t => t.CashierUserId == query.CashierUserId);
        }

        if (query.FromUtc.HasValue)
        {
            q = q.Where(t => t.TransactionDate >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            q = q.Where(t => t.TransactionDate <= query.ToUtc.Value);
        }

        var total = await q.CountAsync(ct);

        var items = await ProjectTransactions(
            q.OrderByDescending(t => t.TransactionDate)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * size)
                .Take(size))
            .ToListAsync(ct);

        return new PagedResult<PosTransactionDto>(items, total, page, size);
    }

    public async Task<PosTransactionDto> ReverseTransactionAsync(
        long transactionId,
        ReversePosTransactionRequest request,
        CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);
        var actorId = currentUser.EnsureAuthenticatedUserId();
        await ValidationHelper.ValidateAndThrowAsync(reverseValidator, request, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var posTx = await db.PosTransactions
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.Id == transactionId, ct)
                ?? throw new EntityNotFoundException(nameof(PosTransaction), transactionId);

            if (posTx.Status != PosTransactionStatus.Completed)
            {
                throw new DuplicateTransactionException(posTx.ReceiptNumber);
            }

            var prevStatus = posTx.Status;
            posTx.Status = request.TargetStatus;
            posTx.VoidOrRefundReason = request.Reason.Trim();
            posTx.ApprovedByUserId = actorId;
            posTx.ApprovedAt = clock.UtcNow;

            bool shouldRestockDirectly = request.TargetStatus == PosTransactionStatus.Voided || request.RestoreToSellableStock;

            foreach (var item in posTx.Items)
            {
                if (shouldRestockDirectly)
                {
                    await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                        ProductId: item.ProductId,
                        QuantityDelta: item.Quantity,
                        SourceLocation: InventoryLocation.ExternalCustomer,
                        DestinationLocation: InventoryLocation.Store,
                        MovementType: InventoryMovementType.TransactionReversal,
                        ReferenceType: nameof(PosTransaction),
                        ReferenceId: posTx.Id.ToString(),
                        ReferenceNumber: posTx.ReceiptNumber,
                        Remarks: $"{request.TargetStatus} reversal ({posTx.ReceiptNumber}): {request.Reason.Trim()}",
                        DeduplicationKey: $"PosReversal:{request.TargetStatus}:Tx:{posTx.Id}:PROD:{item.ProductId}"), ct);
                }
                else
                {
                    // Refunded items not cleared for immediate resale go to inspection gate first.
                    db.WasteAndReturnRecords.Add(new WasteAndReturnRecord
                    {
                        ReferenceNumber = $"RET-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
                        Location = InventoryLocation.Store,
                        ProductId = item.ProductId,
                        PosTransactionId = posTx.Id,
                        Category = WasteReturnCategory.CustomerReturn,
                        Quantity = item.Quantity,
                        Reason = $"Refund {posTx.ReceiptNumber}: {request.Reason.Trim()}",
                        RecordedByUserId = actorId,
                        RecordedAt = clock.UtcNow,
                        InspectionOutcome = InspectionOutcome.PendingInspection,
                        ApprovalStatus = ApprovalStatus.Pending
                    });
                }
            }

            await audit.AddAuditEntryAsync(
                request.TargetStatus == PosTransactionStatus.Voided ? "VoidPosTransaction" : "RefundPosTransaction",
                nameof(PosTransaction),
                posTx.Id.ToString(),
                $"{request.TargetStatus} POS transaction {posTx.ReceiptNumber} (Total: {posTx.TotalAmount:0.00}, Restocked: {shouldRestockDirectly}). Reason: {request.Reason.Trim()}",
                oldValues: new { Status = prevStatus },
                newValues: new { Status = posTx.Status, Restocked = shouldRestockDirectly },
                ct: ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return await ProjectTransactions(db.PosTransactions.AsNoTracking().Where(t => t.Id == transactionId)).FirstAsync(ct);
    }

    private static IQueryable<PosTransactionDto> ProjectTransactions(IQueryable<PosTransaction> source) =>
        source.Select(t => new PosTransactionDto(
            t.Id,
            t.ReceiptNumber,
            t.ClientIdempotencyKey,
            t.CashierUserId,
            t.CashierUser.FullName,
            t.TransactionDate,
            t.SubTotal,
            t.DiscountAmount,
            t.TotalAmount,
            t.PaymentMethod,
            t.AmountTendered,
            t.ChangeAmount,
            t.Status,
            t.VoidOrRefundReason,
            t.ApprovedByUser != null ? t.ApprovedByUser.FullName : null,
            t.ApprovedAt,
            t.Items.Select(i => new PosTransactionItemDto(
                i.Id,
                i.ProductId,
                i.SkuSnapshot,
                i.ProductNameSnapshot,
                i.UnitPrice,
                i.Quantity,
                i.LineTotal)).ToList()));
}
