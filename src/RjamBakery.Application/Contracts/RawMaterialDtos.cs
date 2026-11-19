namespace RjamBakery.Application.Contracts;

public sealed record RawMaterialDto(
    int Id,
    string Sku,
    string Name,
    string Description,
    int UnitId,
    string UnitCode,
    string UnitDimension,
    decimal? PackToBaseRatio,
    decimal CurrentQuantity,
    decimal MinStockLevel,
    int? PreferredSupplierId,
    string? PreferredSupplierName,
    string StockStatus,
    bool IsArchived,
    DateTime UpdatedAt);

public sealed record SaveRawMaterialRequest(
    int? Id,
    string? Sku,
    string Name,
    string Description,
    int UnitId,
    decimal? PackToBaseRatio,
    decimal MinStockLevel,
    int? PreferredSupplierId);

public sealed record RawMaterialQuery(
    string? SearchTerm = null,
    bool LowStockOnly = false,
    bool OutOfStockOnly = false,
    bool IncludeArchived = false,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record RawMaterialAdjustmentRequest(
    int RawMaterialId,
    decimal QuantityDelta,
    int UnitId,
    string Reason,
    string? IdempotencyKey = null,
    bool IsOpeningStock = false);
