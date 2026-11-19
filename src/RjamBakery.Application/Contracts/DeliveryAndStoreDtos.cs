using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Contracts;

public enum DiscrepancyResolutionType
{
    TransitLossWriteOff = 1,
    ReturnToFactoryLot = 2,
    OverageDeductFromFactory = 3
}

public sealed record DeliveryItemDto(
    long Id,
    int ProductId,
    string ProductSku,
    string ProductName,
    string UnitCode,
    long? FactoryStockLotId,
    string? LotNumber,
    decimal RequestedQuantity,
    decimal DispatchedQuantity,
    decimal ReceivedQuantity,
    decimal DiscrepancyQuantity,
    string DiscrepancyReason,
    ReconciliationStatus ReconciliationStatus);

public sealed record DeliveryDto(
    long Id,
    string DeliveryNumber,
    DeliveryStatus Status,
    string CreatedByUserName,
    string? DispatchedByUserName,
    string? ReceivedByUserName,
    DateTime CreatedAt,
    DateTime? DispatchedAt,
    DateTime? ReceivedAt,
    string DriverOrCourierName,
    string Notes,
    IReadOnlyList<DeliveryItemDto> Items);

public sealed record DeliveryItemInput(
    int ProductId,
    decimal RequestedQuantity,
    long? PreferredFactoryLotId = null);

public sealed record CreateDeliveryRequest(
    string DriverOrCourierName,
    string Notes,
    IReadOnlyList<DeliveryItemInput> Items);

public sealed record DispatchDeliveryItemInput(
    long DeliveryItemId,
    decimal DispatchedQuantity);

public sealed record DispatchDeliveryRequest(
    string? DriverOrCourierName = null,
    string? Notes = null,
    IReadOnlyList<DispatchDeliveryItemInput>? ItemQuantities = null);

public sealed record ReceiveDeliveryItemInput(
    long DeliveryItemId,
    decimal ReceivedQuantity,
    string? DiscrepancyReason = null);

public sealed record ConfirmDeliveryReceivingRequest(
    string? Notes,
    IReadOnlyList<ReceiveDeliveryItemInput> Items);

public sealed record ReconcileDeliveryItemInput(
    long DeliveryItemId,
    DiscrepancyResolutionType ResolutionType,
    string ResolutionRemarks);

public sealed record ReconcileDeliveryRequest(
    IReadOnlyList<ReconcileDeliveryItemInput> Items);

public sealed record DeliveryQuery(
    DeliveryStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int PageNumber = 1,
    int PageSize = 25);

// Store Inventory & Adjustments
public sealed record StoreInventoryDto(
    int Id,
    int ProductId,
    string ProductSku,
    string ProductName,
    string CategoryName,
    string UnitCode,
    decimal SellingPrice,
    decimal CurrentQuantity,
    decimal AvailableQuantity,
    decimal MinStockLevel,
    string StockStatus,
    DateTime? LastReceivedAt,
    DateTime? LastSoldAt,
    DateTime UpdatedAt);

public sealed record StoreInventoryQuery(
    string? SearchTerm = null,
    int? CategoryId = null,
    bool LowStockOnly = false,
    bool OutOfStockOnly = false,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record StockAdjustmentRequestDto(
    long Id,
    string ReferenceNumber,
    AdjustmentLocation Location,
    int? ProductId,
    string? ProductName,
    int? RawMaterialId,
    string? RawMaterialName,
    long? FactoryStockLotId,
    decimal QuantityDelta,
    string Reason,
    string RequestedByUserName,
    DateTime RequestedAt,
    ApprovalStatus ApprovalStatus,
    string? ApprovedByUserName,
    DateTime? ApprovedAt);

public sealed record CreateStockAdjustmentRequest(
    AdjustmentLocation Location,
    int? ProductId,
    int? RawMaterialId,
    long? FactoryStockLotId,
    decimal QuantityDelta,
    string Reason,
    string? IdempotencyKey = null);

public sealed record DirectStoreAdjustmentRequest(
    int ProductId,
    decimal QuantityDelta,
    string Reason,
    string? IdempotencyKey = null,
    bool IsOpeningStock = false);

public sealed record ReviewStockAdjustmentRequest(
    bool Approve,
    string? ReviewNotes = null);

public sealed record UpdateStoreThresholdRequest(
    int ProductId,
    decimal MinStockLevel);
