using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<ApplicationUser> Users { get; }
    DbSet<SystemSetting> SystemSettings { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<UnitOfMeasure> UnitsOfMeasure { get; }
    DbSet<ProductCategory> ProductCategories { get; }
    DbSet<Product> Products { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<SupplierRawMaterial> SupplierRawMaterials { get; }
    DbSet<RawMaterial> RawMaterials { get; }
    DbSet<MaterialReceipt> MaterialReceipts { get; }
    DbSet<MaterialReceiptItem> MaterialReceiptItems { get; }
    DbSet<Recipe> Recipes { get; }
    DbSet<RecipeIngredient> RecipeIngredients { get; }
    DbSet<ProductionBatch> ProductionBatches { get; }
    DbSet<ProductionConsumption> ProductionConsumptions { get; }
    DbSet<FactoryStockLot> FactoryStockLots { get; }
    DbSet<Delivery> Deliveries { get; }
    DbSet<DeliveryItem> DeliveryItems { get; }
    DbSet<StoreInventory> StoreInventories { get; }
    DbSet<PosTransaction> PosTransactions { get; }
    DbSet<PosTransactionItem> PosTransactionItems { get; }
    DbSet<WasteAndReturnRecord> WasteAndReturnRecords { get; }
    DbSet<StockAdjustmentRequest> StockAdjustmentRequests { get; }
    DbSet<DailyClosing> DailyClosings { get; }
    DbSet<DailyClosingItem> DailyClosingItems { get; }
    DbSet<InventoryMovement> InventoryMovements { get; }

    DatabaseFacade Database { get; }
    bool HasActiveTransaction { get; }
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface ICurrentUserService
{
    string? UserId { get; }
    string UserName { get; }
    string FullName { get; }
    string PrimaryRole { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
    string EnsureAuthenticatedUserId();
    void EnsureInRole(params string[] allowedRoles);
}

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
    DateTime LocalNow { get; }
    DateOnly TodayLocal { get; }
}
