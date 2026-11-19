using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Contracts;

public sealed record PosCatalogItemDto(
    int ProductId,
    string Sku,
    string Name,
    string Description,
    int CategoryId,
    string CategoryName,
    string UnitCode,
    decimal SellingPrice,
    string? ImagePath,
    decimal AvailableStoreStock,
    decimal MinStockLevel);

public sealed record PosSaleItemInput(
    int ProductId,
    decimal Quantity);

public sealed record CompletePosSaleRequest(
    string ClientIdempotencyKey,
    PaymentMethod PaymentMethod,
    decimal DiscountAmount,
    decimal AmountTendered,
    IReadOnlyList<PosSaleItemInput> Items);

public sealed record PosTransactionItemDto(
    long Id,
    int ProductId,
    string SkuSnapshot,
    string ProductNameSnapshot,
    decimal UnitPrice,
    decimal Quantity,
    decimal LineTotal);

public sealed record PosTransactionDto(
    long Id,
    string ReceiptNumber,
    string ClientIdempotencyKey,
    string CashierUserId,
    string CashierUserName,
    DateTime TransactionDate,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal TotalAmount,
    PaymentMethod PaymentMethod,
    decimal AmountTendered,
    decimal ChangeAmount,
    PosTransactionStatus Status,
    string? VoidOrRefundReason,
    string? ApprovedByUserName,
    DateTime? ApprovedAt,
    IReadOnlyList<PosTransactionItemDto> Items);

public sealed record PosReceiptDto(
    string BakeryName,
    string CurrencySymbol,
    PosTransactionDto Transaction);

public sealed record PosTransactionQuery(
    string? ReceiptNumber = null,
    PosTransactionStatus? Status = null,
    string? CashierUserId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record ReversePosTransactionRequest(
    PosTransactionStatus TargetStatus,
    string Reason,
    bool RestoreToSellableStock = false);
