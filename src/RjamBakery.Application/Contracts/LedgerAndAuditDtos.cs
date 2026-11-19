using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Contracts;

public sealed record RecordRawMaterialMovementRequest(
    int RawMaterialId,
    decimal QuantityDelta,
    int UnitId,
    InventoryLocation SourceLocation,
    InventoryLocation DestinationLocation,
    InventoryMovementType MovementType,
    string ReferenceType,
    string ReferenceId,
    string ReferenceNumber,
    string Remarks,
    string? DeduplicationKey = null,
    bool AllowNegativeStockOverride = false);

public sealed record RecordFactoryLotMovementRequest(
    long FactoryStockLotId,
    decimal QuantityDelta,
    InventoryLocation SourceLocation,
    InventoryLocation DestinationLocation,
    InventoryMovementType MovementType,
    string ReferenceType,
    string ReferenceId,
    string ReferenceNumber,
    string Remarks,
    string? DeduplicationKey = null,
    bool AllowNegativeStockOverride = false);

public sealed record RecordStoreMovementRequest(
    int ProductId,
    decimal QuantityDelta,
    InventoryLocation SourceLocation,
    InventoryLocation DestinationLocation,
    InventoryMovementType MovementType,
    string ReferenceType,
    string ReferenceId,
    string ReferenceNumber,
    string Remarks,
    string? DeduplicationKey = null,
    bool AllowNegativeStockOverride = false,
    bool UpdateLastReceivedAt = false,
    bool UpdateLastSoldAt = false);

public sealed record InventoryMovementDto(
    long Id,
    string MovementNumber,
    InventoryItemType ItemType,
    int? RawMaterialId,
    int? ProductId,
    string ItemName,
    string ItemSku,
    long? FactoryStockLotId,
    InventoryLocation SourceLocation,
    InventoryLocation DestinationLocation,
    InventoryMovementType MovementType,
    decimal QuantityDelta,
    decimal AbsoluteQuantity,
    string UnitCode,
    decimal PreviousQuantity,
    decimal NewQuantity,
    string ReferenceType,
    string ReferenceId,
    string ReferenceNumber,
    DateTime OccurredAt,
    string ResponsibleUserId,
    string ResponsibleUserName,
    string Remarks);

public sealed record InventoryMovementQuery(
    InventoryItemType? ItemType = null,
    int? RawMaterialId = null,
    int? ProductId = null,
    InventoryMovementType? MovementType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? ReferenceNumber = null,
    int PageNumber = 1,
    int PageSize = 50);

public sealed record LedgerMismatchDto(
    string Bucket,
    long EntityId,
    string ItemName,
    decimal CachedBalance,
    decimal LedgerSum);

public sealed record LedgerReconciliationResult(
    bool IsBalanced,
    int CheckedAccounts,
    IReadOnlyList<LedgerMismatchDto> Mismatches);

public sealed record AuditLogDto(
    long Id,
    string UserId,
    string UserName,
    string UserRole,
    string Action,
    string EntityName,
    string EntityId,
    string Summary,
    string? OldValuesJson,
    string? NewValuesJson,
    string? IpAddress,
    DateTime CreatedAt);

public sealed record AuditLogQuery(
    string? UserId = null,
    string? Action = null,
    string? EntityName = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int PageNumber = 1,
    int PageSize = 50);
