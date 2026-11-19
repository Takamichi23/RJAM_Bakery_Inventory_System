using RjamBakery.Domain.Enums;

namespace RjamBakery.Domain.Entities;

public class UnitOfMeasure
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public UnitDimension Dimension { get; set; }
    public decimal FactorToBase { get; set; }
    public bool IsBaseUnit { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProductCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Product> Products { get; set; } = [];
}

public class Product
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int BaseUnitId { get; set; }
    public decimal SellingPrice { get; set; }
    public decimal MinStockLevel { get; set; }
    public int? ShelfLifeDays { get; set; }
    public string? ImagePath { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ProductCategory Category { get; set; } = null!;
    public UnitOfMeasure BaseUnit { get; set; } = null!;
    public StoreInventory? StoreInventory { get; set; }
    public ICollection<Recipe> Recipes { get; set; } = [];
    public ICollection<FactoryStockLot> FactoryStockLots { get; set; } = [];
}

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public SupplierStatus Status { get; set; } = SupplierStatus.Active;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SupplierRawMaterial> SuppliedMaterials { get; set; } = [];
    public ICollection<MaterialReceipt> MaterialReceipts { get; set; } = [];
}

public class RawMaterial
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int UnitId { get; set; }
    /// <summary>
    /// Optional pack-to-base ratio (e.g., 1 pack/sack = 25 kg or 30 pcs) when materials are received or batched in packs.
    /// </summary>
    public decimal? PackToBaseRatio { get; set; }
    public decimal CurrentQuantity { get; set; }
    public decimal MinStockLevel { get; set; }
    public int? PreferredSupplierId { get; set; }
    public bool IsArchived { get; set; }
    public int RowVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public UnitOfMeasure Unit { get; set; } = null!;
    public Supplier? PreferredSupplier { get; set; }
    public ICollection<SupplierRawMaterial> Suppliers { get; set; } = [];
}

public class SupplierRawMaterial
{
    public int SupplierId { get; set; }
    public int RawMaterialId { get; set; }
    public decimal? DefaultUnitCost { get; set; }
    public int? LeadTimeDays { get; set; }

    public Supplier Supplier { get; set; } = null!;
    public RawMaterial RawMaterial { get; set; } = null!;
}

public class MaterialReceipt
{
    public long Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public int SupplierId { get; set; }
    public DateTime ReceivingDate { get; set; } = DateTime.UtcNow;
    public string ReceivedByUserId { get; set; } = string.Empty;
    public MaterialReceiptStatus Status { get; set; } = MaterialReceiptStatus.Draft;
    public decimal TotalCost { get; set; }
    public string Remarks { get; set; } = string.Empty;
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedByUserId { get; set; }

    public Supplier Supplier { get; set; } = null!;
    public ApplicationUser ReceivedByUser { get; set; } = null!;
    public ApplicationUser? ConfirmedByUser { get; set; }
    public ICollection<MaterialReceiptItem> Items { get; set; } = [];
}

public class MaterialReceiptItem
{
    public long Id { get; set; }
    public long MaterialReceiptId { get; set; }
    public int RawMaterialId { get; set; }
    public decimal Quantity { get; set; }
    public int UnitId { get; set; }
    public decimal ConvertedBaseQuantity { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal LineTotal { get; set; }

    public MaterialReceipt MaterialReceipt { get; set; } = null!;
    public RawMaterial RawMaterial { get; set; } = null!;
    public UnitOfMeasure Unit { get; set; } = null!;
}
