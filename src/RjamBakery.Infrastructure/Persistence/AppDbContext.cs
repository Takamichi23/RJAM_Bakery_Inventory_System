using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RjamBakery.Application.Abstractions;
using RjamBakery.Domain.Entities;

namespace RjamBakery.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options), IAppDbContext
{
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierRawMaterial> SupplierRawMaterials => Set<SupplierRawMaterial>();
    public DbSet<RawMaterial> RawMaterials => Set<RawMaterial>();
    public DbSet<MaterialReceipt> MaterialReceipts => Set<MaterialReceipt>();
    public DbSet<MaterialReceiptItem> MaterialReceiptItems => Set<MaterialReceiptItem>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();
    public DbSet<ProductionBatch> ProductionBatches => Set<ProductionBatch>();
    public DbSet<ProductionConsumption> ProductionConsumptions => Set<ProductionConsumption>();
    public DbSet<FactoryStockLot> FactoryStockLots => Set<FactoryStockLot>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryItem> DeliveryItems => Set<DeliveryItem>();
    public DbSet<StoreInventory> StoreInventories => Set<StoreInventory>();
    public DbSet<PosTransaction> PosTransactions => Set<PosTransaction>();
    public DbSet<PosTransactionItem> PosTransactionItems => Set<PosTransactionItem>();
    public DbSet<WasteAndReturnRecord> WasteAndReturnRecords => Set<WasteAndReturnRecord>();
    public DbSet<StockAdjustmentRequest> StockAdjustmentRequests => Set<StockAdjustmentRequest>();
    public DbSet<DailyClosing> DailyClosings => Set<DailyClosing>();
    public DbSet<DailyClosingItem> DailyClosingItems => Set<DailyClosingItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    public bool HasActiveTransaction => Database.CurrentTransaction is not null;

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Database.BeginTransactionAsync(ct);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        IncrementConcurrencyVersions();
        return base.SaveChangesAsync(ct);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Ensure Identity key/index columns remain well within utf8mb4 index limits across MySQL 8.0 and MariaDB 10.4.
        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.NormalizedUserName).HasMaxLength(128);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.NormalizedEmail).HasMaxLength(160);
            e.Property(x => x.FullName).HasMaxLength(120).IsRequired();
        });

        builder.Entity<IdentityRole>(e =>
        {
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.NormalizedName).HasMaxLength(128);
        });

        builder.Entity<IdentityUserLogin<string>>(e =>
        {
            e.Property(x => x.LoginProvider).HasMaxLength(128);
            e.Property(x => x.ProviderKey).HasMaxLength(128);
        });

        builder.Entity<IdentityUserToken<string>>(e =>
        {
            e.Property(x => x.LoginProvider).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(128);
        });
    }

    private void IncrementConcurrencyVersions()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            switch (entry.Entity)
            {
                case RawMaterial rm:
                    rm.RowVersion++;
                    break;
                case FactoryStockLot lot:
                    lot.RowVersion++;
                    break;
                case StoreInventory inv:
                    inv.RowVersion++;
                    break;
            }
        }
    }
}
