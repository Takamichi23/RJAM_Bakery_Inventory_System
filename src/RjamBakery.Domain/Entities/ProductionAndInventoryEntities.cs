using RjamBakery.Domain.Enums;

namespace RjamBakery.Domain.Entities;

public class Recipe
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public decimal BatchYieldQuantity { get; set; }
    public int YieldUnitId { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Product Product { get; set; } = null!;
    public UnitOfMeasure YieldUnit { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ICollection<RecipeIngredient> Ingredients { get; set; } = [];
}

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public int RawMaterialId { get; set; }
    public decimal QuantityRequired { get; set; }
    public int UnitId { get; set; }

    public Recipe Recipe { get; set; } = null!;
    public RawMaterial RawMaterial { get; set; } = null!;
    public UnitOfMeasure Unit { get; set; } = null!;
}

public class ProductionBatch
{
    public long Id { get; set; }
    public string BatchReference { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public int? RecipeId { get; set; }
    public int? RecipeVersionSnapshot { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal ActualQuantity { get; set; }
    public DateOnly ProductionDate { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string BakerUserId { get; set; } = string.Empty;
    public ProductionBatchStatus Status { get; set; } = ProductionBatchStatus.Planned;
    public string Remarks { get; set; } = string.Empty;

    public Product Product { get; set; } = null!;
    public Recipe? Recipe { get; set; }
    public ApplicationUser BakerUser { get; set; } = null!;
    public ICollection<ProductionConsumption> Consumptions { get; set; } = [];
    public ICollection<FactoryStockLot> OutputLots { get; set; } = [];
}

public class ProductionConsumption
{
    public long Id { get; set; }
    public long ProductionBatchId { get; set; }
    public int RawMaterialId { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal ActualConsumedQuantity { get; set; }
    public int UnitId { get; set; }
    public string SnapshotRawMaterialName { get; set; } = string.Empty;
    public decimal SnapshotRecipeQuantity { get; set; }

    public ProductionBatch ProductionBatch { get; set; } = null!;
    public RawMaterial RawMaterial { get; set; } = null!;
    public UnitOfMeasure Unit { get; set; } = null!;
}

public class FactoryStockLot
{
    public long Id { get; set; }
    public string LotNumber { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public long? ProductionBatchId { get; set; }
    public DateOnly ProductionDate { get; set; }
    public DateOnly? ExpirationDate { get; set; }
    public decimal InitialQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public FactoryLotStatus Status { get; set; } = FactoryLotStatus.Available;
    public int RowVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Product Product { get; set; } = null!;
    public ProductionBatch? ProductionBatch { get; set; }
}

public class Delivery
{
    public long Id { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Draft;
    public string CreatedByUserId { get; set; } = string.Empty;
    public string? DispatchedByUserId { get; set; }
    public string? ReceivedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DispatchedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string DriverOrCourierName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? DispatchedByUser { get; set; }
    public ApplicationUser? ReceivedByUser { get; set; }
    public ICollection<DeliveryItem> Items { get; set; } = [];
}

public class DeliveryItem
{
    public long Id { get; set; }
    public long DeliveryId { get; set; }
    public int ProductId { get; set; }
    public long? FactoryStockLotId { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal DispatchedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal DiscrepancyQuantity { get; set; }
    public string DiscrepancyReason { get; set; } = string.Empty;
    public ReconciliationStatus ReconciliationStatus { get; set; } = ReconciliationStatus.None;

    public Delivery Delivery { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public FactoryStockLot? FactoryStockLot { get; set; }
}

public class StoreInventory
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public decimal CurrentQuantity { get; set; }
    public decimal MinStockLevel { get; set; }
    public DateTime? LastReceivedAt { get; set; }
    public DateTime? LastSoldAt { get; set; }
    public int RowVersion { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Product Product { get; set; } = null!;
}
