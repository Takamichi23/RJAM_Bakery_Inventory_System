using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default);
}

public sealed class DashboardService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    ISystemSettingService settingService,
    IDateTimeProvider clock) : IDashboardService
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        currentUser.EnsureAuthenticatedUserId();

        var opConfig = await settingService.GetOperationalConfigAsync(ct);
        var todayLocal = clock.TodayLocal;
        var todayUtcStart = DateTime.SpecifyKind(todayLocal.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime();
        var todayUtcEnd = DateTime.SpecifyKind(todayLocal.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Local).ToUniversalTime();

        var totalProducts = await db.Products.AsNoTracking().CountAsync(p => !p.IsArchived, ct);

        var rawMaterials = await db.RawMaterials.AsNoTracking()
            .Where(m => !m.IsArchived)
            .Select(m => new { m.CurrentQuantity, m.MinStockLevel })
            .ToListAsync(ct);

        var factoryLots = await db.FactoryStockLots.AsNoTracking()
            .Where(l => l.Status == FactoryLotStatus.Available && l.RemainingQuantity > 0)
            .Select(l => new { l.ProductId, Available = l.RemainingQuantity - l.ReservedQuantity })
            .ToListAsync(ct);

        var storeBalances = await db.Products.AsNoTracking()
            .Where(p => !p.IsArchived)
            .Select(p => new
            {
                Current = p.StoreInventory != null ? p.StoreInventory.CurrentQuantity : 0m,
                Min = p.StoreInventory != null ? p.StoreInventory.MinStockLevel : p.MinStockLevel
            })
            .ToListAsync(ct);

        var todayProd = await db.ProductionBatches.AsNoTracking()
            .Where(b => b.Status == ProductionBatchStatus.Completed && b.ProductionDate == todayLocal)
            .Select(b => b.ActualQuantity)
            .ToListAsync(ct);

        var pendingDeliveries = await db.Deliveries.AsNoTracking()
            .CountAsync(d => d.Status == DeliveryStatus.Draft || d.Status == DeliveryStatus.PendingDispatch, ct);

        var dispatchedDeliveries = await db.Deliveries.AsNoTracking()
            .CountAsync(d => d.Status == DeliveryStatus.Dispatched, ct);

        var todaySales = await db.PosTransactions.AsNoTracking()
            .Where(t => t.Status == PosTransactionStatus.Completed
                        && t.TransactionDate >= todayUtcStart
                        && t.TransactionDate <= todayUtcEnd)
            .Select(t => t.TotalAmount)
            .ToListAsync(ct);

        var openDeliveryDiscrepancies = await db.Deliveries.AsNoTracking()
            .CountAsync(d => d.Status == DeliveryStatus.PartiallyReceived, ct);

        var pendingClosingVariances = await db.DailyClosings.AsNoTracking()
            .CountAsync(c => c.Status == DailyClosingStatus.VarianceReviewPending, ct);

        var pendingReturns = await db.WasteAndReturnRecords.AsNoTracking()
            .CountAsync(w => w.Category == WasteReturnCategory.CustomerReturn && w.InspectionOutcome == InspectionOutcome.PendingInspection, ct);

        var pendingAdjustments = await db.StockAdjustmentRequests.AsNoTracking()
            .CountAsync(a => a.ApprovalStatus == ApprovalStatus.Pending, ct);

        int rawLow = rawMaterials.Count(m => m.CurrentQuantity > 0 && m.CurrentQuantity <= m.MinStockLevel);
        int rawOut = rawMaterials.Count(m => m.CurrentQuantity <= 0);
        int storeLow = storeBalances.Count(s => s.Current > 0 && s.Current <= s.Min);
        int storeOut = storeBalances.Count(s => s.Current <= 0);

        int lowStockCount = currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            ? rawLow
            : currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
                ? storeLow
                : rawLow + storeLow;

        int outStockCount = currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
            ? rawOut
            : currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager)
                ? storeOut
                : rawOut + storeOut;

        var alerts = new List<DashboardAlertDto>();
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager) || currentUser.IsInRole(AppRoles.Baker)) && (rawLow + rawOut) > 0)
        {
            alerts.Add(new DashboardAlertDto("Warning", "Raw Material Stock Alert", $"{rawLow} low-stock and {rawOut} out-of-stock raw materials.", "/raw-materials"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager) || currentUser.IsInRole(AppRoles.Cashier)) && (storeLow + storeOut) > 0)
        {
            alerts.Add(new DashboardAlertDto("Warning", "Store Stock Alert", $"{storeLow} low-stock and {storeOut} out-of-stock store items.", "/store-inventory"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager) || currentUser.IsInRole(AppRoles.Cashier)) && dispatchedDeliveries > 0)
        {
            alerts.Add(new DashboardAlertDto("Info", "Incoming Deliveries", $"{dispatchedDeliveries} dispatched delivery(s) awaiting store receiving.", "/deliveries"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager)) && openDeliveryDiscrepancies > 0)
        {
            alerts.Add(new DashboardAlertDto("Error", "Delivery Discrepancies", $"{openDeliveryDiscrepancies} delivery(s) require discrepancy reconciliation.", "/deliveries"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager)) && pendingReturns > 0)
        {
            alerts.Add(new DashboardAlertDto("Warning", "Returns Pending Inspection", $"{pendingReturns} customer return(s) waiting for physical inspection.", "/returns-waste"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager)) && pendingAdjustments > 0)
        {
            alerts.Add(new DashboardAlertDto("Warning", "Stock Adjustments Pending", $"{pendingAdjustments} stock adjustment request(s) awaiting approval.", "/store-inventory"));
        }
        if ((currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager)) && pendingClosingVariances > 0)
        {
            alerts.Add(new DashboardAlertDto("Error", "Closing Variances Pending", $"{pendingClosingVariances} daily closing count(s) require manager review.", "/daily-closing"));
        }

        // Role-scoped recent ledger activities
        var moveQuery = db.InventoryMovements.AsNoTracking();
        if (currentUser.IsInRole(AppRoles.Baker) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            moveQuery = moveQuery.Where(m => m.ItemType == InventoryItemType.RawMaterial || m.SourceLocation == InventoryLocation.Factory || m.DestinationLocation == InventoryLocation.Factory);
        }
        else if (currentUser.IsInRole(AppRoles.Cashier) && !currentUser.IsInRole(AppRoles.Owner) && !currentUser.IsInRole(AppRoles.Manager))
        {
            moveQuery = moveQuery.Where(m => m.SourceLocation == InventoryLocation.Store || m.DestinationLocation == InventoryLocation.Store);
        }

        var recent = await moveQuery
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.Id)
            .Take(8)
            .Select(m => new RecentActivityDto(
                m.OccurredAt,
                m.MovementNumber,
                m.RawMaterial != null ? m.RawMaterial.Name : (m.Product != null ? m.Product.Name : "Item"),
                m.MovementType.ToString(),
                m.QuantityDelta,
                m.Unit.Code,
                m.ReferenceNumber,
                m.ResponsibleUser.FullName))
            .ToListAsync(ct);

        bool canSeeSales = currentUser.IsInRole(AppRoles.Owner) || currentUser.IsInRole(AppRoles.Manager) || currentUser.IsInRole(AppRoles.Cashier);

        return new DashboardSummaryDto(
            ActiveRole: currentUser.PrimaryRole,
            BakeryName: opConfig.BakeryName,
            CurrencySymbol: opConfig.CurrencySymbol,
            TotalActiveProducts: totalProducts,
            TotalRawMaterialUnits: rawMaterials.Sum(m => m.CurrentQuantity),
            TotalFactoryFinishedUnits: factoryLots.Sum(l => l.Available),
            TotalStoreSellableUnits: storeBalances.Sum(s => s.Current),
            LowStockCount: lowStockCount,
            OutOfStockCount: outStockCount,
            TodayProductionUnits: todayProd.Sum(),
            TodayProductionBatches: todayProd.Count,
            PendingDeliveriesCount: pendingDeliveries,
            DispatchedDeliveriesCount: dispatchedDeliveries,
            TodayNetSales: canSeeSales ? todaySales.Sum() : 0m,
            TodayTransactionCount: canSeeSales ? todaySales.Count : 0,
            OpenDiscrepanciesCount: openDeliveryDiscrepancies + pendingClosingVariances,
            PendingReturnsInspectionCount: pendingReturns,
            PendingAdjustmentsCount: pendingAdjustments,
            PendingClosingVariancesCount: pendingClosingVariances,
            Alerts: alerts,
            RecentActivities: recent);
    }
}
