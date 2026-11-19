using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Contracts;

// Suppliers
public sealed record SupplierMaterialDto(
    int RawMaterialId,
    string RawMaterialSku,
    string RawMaterialName,
    string UnitCode,
    decimal? DefaultUnitCost,
    int? LeadTimeDays);

public sealed record SupplierDto(
    int Id,
    string Name,
    string ContactPerson,
    string Phone,
    string Email,
    string Address,
    SupplierStatus Status,
    string Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<SupplierMaterialDto> SuppliedMaterials);

public sealed record SupplierMaterialLinkRequest(
    int RawMaterialId,
    decimal? DefaultUnitCost,
    int? LeadTimeDays);

public sealed record SaveSupplierRequest(
    int? Id,
    string Name,
    string ContactPerson,
    string Phone,
    string Email,
    string Address,
    SupplierStatus Status,
    string Notes,
    IReadOnlyList<SupplierMaterialLinkRequest>? SuppliedMaterials = null);

// Material Receiving
public sealed record MaterialReceiptItemDto(
    long Id,
    int RawMaterialId,
    string RawMaterialSku,
    string RawMaterialName,
    decimal Quantity,
    int UnitId,
    string UnitCode,
    decimal ConvertedBaseQuantity,
    string BaseUnitCode,
    decimal? UnitCost,
    decimal LineTotal);

public sealed record MaterialReceiptDto(
    long Id,
    string ReceiptNumber,
    int SupplierId,
    string SupplierName,
    DateTime ReceivingDate,
    string ReceivedByUserId,
    string ReceivedByUserName,
    MaterialReceiptStatus Status,
    decimal TotalCost,
    string Remarks,
    DateTime? ConfirmedAt,
    string? ConfirmedByUserName,
    IReadOnlyList<MaterialReceiptItemDto> Items);

public sealed record MaterialReceiptItemInput(
    int RawMaterialId,
    decimal Quantity,
    int UnitId,
    decimal? UnitCost);

public sealed record CreateMaterialReceiptRequest(
    int SupplierId,
    DateTime ReceivingDate,
    string Remarks,
    IReadOnlyList<MaterialReceiptItemInput> Items);

public sealed record MaterialReceiptQuery(
    int? SupplierId = null,
    MaterialReceiptStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int PageNumber = 1,
    int PageSize = 25);

// Recipes (BOM)
public sealed record RecipeIngredientDto(
    int Id,
    int RawMaterialId,
    string RawMaterialSku,
    string RawMaterialName,
    decimal QuantityRequired,
    int UnitId,
    string UnitCode);

public sealed record RecipeDto(
    int Id,
    int ProductId,
    string ProductSku,
    string ProductName,
    int VersionNumber,
    decimal BatchYieldQuantity,
    int YieldUnitId,
    string YieldUnitCode,
    bool IsActive,
    string Notes,
    string CreatedByUserName,
    DateTime CreatedAt,
    IReadOnlyList<RecipeIngredientDto> Ingredients);

public sealed record RecipeIngredientInput(
    int RawMaterialId,
    decimal QuantityRequired,
    int UnitId);

public sealed record SaveRecipeRequest(
    int ProductId,
    decimal BatchYieldQuantity,
    int YieldUnitId,
    string Notes,
    IReadOnlyList<RecipeIngredientInput> Ingredients);

public sealed record CalculatedIngredientRequirementDto(
    int RawMaterialId,
    string RawMaterialSku,
    string RawMaterialName,
    decimal RecipeConfiguredQuantity,
    string RecipeUnitCode,
    decimal RequiredInBaseUnit,
    decimal AvailableInBaseUnit,
    string BaseUnitCode,
    bool IsSufficient,
    decimal ShortfallInBaseUnit);

public sealed record ProductionRequirementPreviewDto(
    int ProductId,
    string ProductName,
    int? RecipeId,
    int? RecipeVersionNumber,
    decimal TargetOutputQuantity,
    string OutputUnitCode,
    bool AllIngredientsSufficient,
    bool IsOutsideFactoryHours,
    IReadOnlyList<CalculatedIngredientRequirementDto> Ingredients);

// Production
public sealed record ProductionConsumptionDto(
    long Id,
    int RawMaterialId,
    string SnapshotRawMaterialName,
    decimal PlannedQuantity,
    decimal ActualConsumedQuantity,
    int UnitId,
    string UnitCode,
    decimal SnapshotRecipeQuantity);

public sealed record ProductionBatchDto(
    long Id,
    string BatchReference,
    int ProductId,
    string ProductSku,
    string ProductName,
    int? RecipeId,
    int? RecipeVersionSnapshot,
    decimal PlannedQuantity,
    decimal ActualQuantity,
    string OutputUnitCode,
    DateOnly ProductionDate,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string BakerUserId,
    string BakerUserName,
    ProductionBatchStatus Status,
    string Remarks,
    IReadOnlyList<ProductionConsumptionDto> Consumptions);

public sealed record ProductionConsumptionInput(
    int RawMaterialId,
    decimal ActualConsumedQuantity,
    int UnitId);

public sealed record CreateProductionPlanRequest(
    int ProductId,
    decimal PlannedQuantity,
    DateOnly ProductionDate,
    string Remarks);

public sealed record FinalizeProductionRequest(
    long? ExistingBatchId,
    int ProductId,
    decimal PlannedQuantity,
    decimal ActualOutputQuantity,
    DateOnly ProductionDate,
    DateOnly? ExpirationDate,
    string Remarks,
    IReadOnlyList<ProductionConsumptionInput> Consumptions,
    bool ConfirmNoRecipe = false,
    string? IdempotencyKey = null);

public sealed record ProductionQuery(
    int? ProductId = null,
    ProductionBatchStatus? Status = null,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    int PageNumber = 1,
    int PageSize = 25);

// Factory Finished Goods Inventory
public sealed record FactoryStockLotDto(
    long Id,
    string LotNumber,
    int ProductId,
    string ProductSku,
    string ProductName,
    string UnitCode,
    long? ProductionBatchId,
    string? BatchReference,
    DateOnly ProductionDate,
    DateOnly? ExpirationDate,
    decimal InitialQuantity,
    decimal RemainingQuantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity,
    FactoryLotStatus Status,
    string DisplayStatus);

public sealed record FactoryProductSummaryDto(
    int ProductId,
    string ProductSku,
    string ProductName,
    string UnitCode,
    decimal TotalRemainingQuantity,
    decimal TotalReservedQuantity,
    decimal TotalAvailableQuantity,
    decimal MinStockLevel,
    int ActiveLotCount,
    DateOnly? EarliestExpirationDate,
    string StockStatus);

public sealed record FactoryLotQuery(
    int? ProductId = null,
    FactoryLotStatus? Status = null,
    bool OnlyAvailable = false,
    int PageNumber = 1,
    int PageSize = 50);

public sealed record FefoAllocationPreviewDto(
    long FactoryStockLotId,
    string LotNumber,
    DateOnly ProductionDate,
    DateOnly? ExpirationDate,
    decimal AllocatedQuantity);
