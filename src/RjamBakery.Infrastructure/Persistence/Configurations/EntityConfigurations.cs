using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Infrastructure.Persistence.Configurations;

public sealed class SystemAndAuditConfiguration :
    IEntityTypeConfiguration<SystemSetting>,
    IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SettingKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SettingValue).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.SettingKey).IsUnique();
        builder.HasOne(x => x.UpdatedByUser).WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.UserRole).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(80).IsRequired();
        builder.Property(x => x.EntityName).HasMaxLength(80).IsRequired();
        builder.Property(x => x.EntityId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(500).IsRequired();
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => new { x.EntityName, x.EntityId });
        builder.HasIndex(x => x.UserId);
        builder.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CatalogAndMaterialConfiguration :
    IEntityTypeConfiguration<UnitOfMeasure>,
    IEntityTypeConfiguration<ProductCategory>,
    IEntityTypeConfiguration<Product>,
    IEntityTypeConfiguration<Supplier>,
    IEntityTypeConfiguration<RawMaterial>,
    IEntityTypeConfiguration<SupplierRawMaterial>,
    IEntityTypeConfiguration<MaterialReceipt>,
    IEntityTypeConfiguration<MaterialReceiptItem>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(60).IsRequired();
        builder.Property(x => x.FactorToBase).HasPrecision(18, 6);
        builder.HasIndex(x => x.Code).IsUnique();
    }

    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.Name).IsUnique();
    }

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Sku).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SellingPrice).HasPrecision(18, 2);
        builder.Property(x => x.MinStockLevel).HasPrecision(18, 4);
        builder.Property(x => x.ImagePath).HasMaxLength(300);
        builder.HasIndex(x => x.Sku).IsUnique();
        builder.HasIndex(x => x.Name);
        builder.HasOne(x => x.Category).WithMany(c => c.Products).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.BaseUnit).WithMany().HasForeignKey(x => x.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.ContactPerson).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Address).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.Name);
    }

    public void Configure(EntityTypeBuilder<RawMaterial> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Sku).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.PackToBaseRatio).HasPrecision(18, 4);
        builder.Property(x => x.CurrentQuantity).HasPrecision(18, 4);
        builder.Property(x => x.MinStockLevel).HasPrecision(18, 4);
        builder.Property(x => x.RowVersion).IsConcurrencyToken();
        builder.HasIndex(x => x.Sku).IsUnique();
        builder.HasIndex(x => x.Name);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PreferredSupplier).WithMany().HasForeignKey(x => x.PreferredSupplierId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<SupplierRawMaterial> builder)
    {
        builder.HasKey(x => new { x.SupplierId, x.RawMaterialId });
        builder.Property(x => x.DefaultUnitCost).HasPrecision(18, 2);
        builder.HasOne(x => x.Supplier).WithMany(s => s.SuppliedMaterials).HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.RawMaterial).WithMany(m => m.Suppliers).HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<MaterialReceipt> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ReceiptNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TotalCost).HasPrecision(18, 2);
        builder.Property(x => x.Remarks).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.ReceiptNumber).IsUnique();
        builder.HasOne(x => x.Supplier).WithMany(s => s.MaterialReceipts).HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReceivedByUser).WithMany().HasForeignKey(x => x.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ConfirmedByUser).WithMany().HasForeignKey(x => x.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<MaterialReceiptItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.ConvertedBaseQuantity).HasPrecision(18, 4);
        builder.Property(x => x.UnitCost).HasPrecision(18, 2);
        builder.Property(x => x.LineTotal).HasPrecision(18, 2);
        builder.HasOne(x => x.MaterialReceipt).WithMany(r => r.Items).HasForeignKey(x => x.MaterialReceiptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProductionDeliveryAndStoreConfiguration :
    IEntityTypeConfiguration<Recipe>,
    IEntityTypeConfiguration<RecipeIngredient>,
    IEntityTypeConfiguration<ProductionBatch>,
    IEntityTypeConfiguration<ProductionConsumption>,
    IEntityTypeConfiguration<FactoryStockLot>,
    IEntityTypeConfiguration<Delivery>,
    IEntityTypeConfiguration<DeliveryItem>,
    IEntityTypeConfiguration<StoreInventory>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BatchYieldQuantity).HasPrecision(18, 4);
        builder.Property(x => x.Notes).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => new { x.ProductId, x.VersionNumber }).IsUnique();
        builder.HasOne(x => x.Product).WithMany(p => p.Recipes).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.YieldUnit).WithMany().HasForeignKey(x => x.YieldUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.QuantityRequired).HasPrecision(18, 4);
        builder.HasOne(x => x.Recipe).WithMany(r => r.Ingredients).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionBatch> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BatchReference).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PlannedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.ActualQuantity).HasPrecision(18, 4);
        builder.Property(x => x.Remarks).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.BatchReference).IsUnique();
        builder.HasIndex(x => x.ProductionDate);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.BakerUser).WithMany().HasForeignKey(x => x.BakerUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionConsumption> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PlannedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.ActualConsumedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.SnapshotRecipeQuantity).HasPrecision(18, 4);
        builder.Property(x => x.SnapshotRawMaterialName).HasMaxLength(150).IsRequired();
        builder.HasOne(x => x.ProductionBatch).WithMany(b => b.Consumptions).HasForeignKey(x => x.ProductionBatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<FactoryStockLot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LotNumber).HasMaxLength(50).IsRequired();
        builder.Property(x => x.InitialQuantity).HasPrecision(18, 4);
        builder.Property(x => x.RemainingQuantity).HasPrecision(18, 4);
        builder.Property(x => x.ReservedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.RowVersion).IsConcurrencyToken();
        builder.HasIndex(x => x.LotNumber).IsUnique();
        builder.HasIndex(x => new { x.ProductId, x.Status, x.ExpirationDate, x.ProductionDate });
        builder.HasOne(x => x.Product).WithMany(p => p.FactoryStockLots).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionBatch).WithMany(b => b.OutputLots).HasForeignKey(x => x.ProductionBatchId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DeliveryNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.DriverOrCourierName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.DeliveryNumber).IsUnique();
        builder.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DispatchedByUser).WithMany().HasForeignKey(x => x.DispatchedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReceivedByUser).WithMany().HasForeignKey(x => x.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<DeliveryItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RequestedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.DispatchedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.ReceivedQuantity).HasPrecision(18, 4);
        builder.Property(x => x.DiscrepancyQuantity).HasPrecision(18, 4);
        builder.Property(x => x.DiscrepancyReason).HasMaxLength(300).IsRequired();
        builder.HasOne(x => x.Delivery).WithMany(d => d.Items).HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FactoryStockLot).WithMany().HasForeignKey(x => x.FactoryStockLotId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<StoreInventory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CurrentQuantity).HasPrecision(18, 4);
        builder.Property(x => x.MinStockLevel).HasPrecision(18, 4);
        builder.Property(x => x.RowVersion).IsConcurrencyToken();
        builder.HasIndex(x => x.ProductId).IsUnique();
        builder.HasOne(x => x.Product).WithOne(p => p.StoreInventory).HasForeignKey<StoreInventory>(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PosClosingAndLedgerConfiguration :
    IEntityTypeConfiguration<PosTransaction>,
    IEntityTypeConfiguration<PosTransactionItem>,
    IEntityTypeConfiguration<WasteAndReturnRecord>,
    IEntityTypeConfiguration<StockAdjustmentRequest>,
    IEntityTypeConfiguration<DailyClosing>,
    IEntityTypeConfiguration<DailyClosingItem>,
    IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<PosTransaction> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ReceiptNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ClientIdempotencyKey).HasMaxLength(80).IsRequired();
        builder.Property(x => x.SubTotal).HasPrecision(18, 2);
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 2);
        builder.Property(x => x.TotalAmount).HasPrecision(18, 2);
        builder.Property(x => x.AmountTendered).HasPrecision(18, 2);
        builder.Property(x => x.ChangeAmount).HasPrecision(18, 2);
        builder.Property(x => x.VoidOrRefundReason).HasMaxLength(300);
        builder.HasIndex(x => x.ReceiptNumber).IsUnique();
        builder.HasIndex(x => x.ClientIdempotencyKey).IsUnique();
        builder.HasIndex(x => x.TransactionDate);
        builder.HasOne(x => x.CashierUser).WithMany().HasForeignKey(x => x.CashierUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<PosTransactionItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(150).IsRequired();
        builder.Property(x => x.SkuSnapshot).HasMaxLength(50).IsRequired();
        builder.Property(x => x.UnitPrice).HasPrecision(18, 2);
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.LineTotal).HasPrecision(18, 2);
        builder.HasOne(x => x.PosTransaction).WithMany(t => t.Items).HasForeignKey(x => x.PosTransactionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<WasteAndReturnRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ReferenceNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.ReferenceNumber).IsUnique();
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FactoryStockLot).WithMany().HasForeignKey(x => x.FactoryStockLotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PosTransaction).WithMany().HasForeignKey(x => x.PosTransactionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RecordedByUser).WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<StockAdjustmentRequest> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ReferenceNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.QuantityDelta).HasPrecision(18, 4);
        builder.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.ReferenceNumber).IsUnique();
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FactoryStockLot).WithMany().HasForeignKey(x => x.FactoryStockLotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DailyClosing).WithMany(c => c.VarianceAdjustments).HasForeignKey(x => x.DailyClosingId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<DailyClosing> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ClosingNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.ClosingNumber).IsUnique();
        builder.HasIndex(x => x.OperatingDate).IsUnique();
        builder.HasOne(x => x.SubmittedByUser).WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<DailyClosingItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OpeningStock).HasPrecision(18, 4);
        builder.Property(x => x.ConfirmedDeliveries).HasPrecision(18, 4);
        builder.Property(x => x.ApprovedRestockedReturns).HasPrecision(18, 4);
        builder.Property(x => x.TotalSold).HasPrecision(18, 4);
        builder.Property(x => x.RecordedWaste).HasPrecision(18, 4);
        builder.Property(x => x.ApprovedAdjustments).HasPrecision(18, 4);
        builder.Property(x => x.ExpectedClosingStock).HasPrecision(18, 4);
        builder.Property(x => x.ActualClosingStock).HasPrecision(18, 4);
        builder.Property(x => x.VarianceQuantity).HasPrecision(18, 4);
        builder.Property(x => x.CarryoverToNextDay).HasPrecision(18, 4);
        builder.Property(x => x.VarianceReason).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => new { x.DailyClosingId, x.ProductId }).IsUnique();
        builder.HasOne(x => x.DailyClosing).WithMany(c => c.Items).HasForeignKey(x => x.DailyClosingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MovementNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.DeduplicationKey).HasMaxLength(180).IsRequired();
        builder.Property(x => x.QuantityDelta).HasPrecision(18, 4);
        builder.Property(x => x.AbsoluteQuantity).HasPrecision(18, 4);
        builder.Property(x => x.PreviousQuantity).HasPrecision(18, 4);
        builder.Property(x => x.NewQuantity).HasPrecision(18, 4);
        builder.Property(x => x.ReferenceType).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ReferenceId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.ReferenceNumber).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Remarks).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.MovementNumber).IsUnique();
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => new { x.ItemType, x.RawMaterialId });
        builder.HasIndex(x => new { x.ItemType, x.ProductId });
        builder.HasOne(x => x.RawMaterial).WithMany().HasForeignKey(x => x.RawMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FactoryStockLot).WithMany().HasForeignKey(x => x.FactoryStockLotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
