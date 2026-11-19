using Microsoft.EntityFrameworkCore;
using RjamBakery.Application.Abstractions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Services;

public interface IReportingService
{
    Task<InventoryReportDto> GetInventoryReportAsync(CancellationToken ct = default);
    Task<ProductionReportDto> GetProductionReportAsync(DateOnly fromDate, DateOnly toDate, int? productId = null, CancellationToken ct = default);
    Task<SalesReportDto> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, int? productId = null, CancellationToken ct = default);
    Task<DeliveryReportDto> GetDeliveryReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<ClosingReportDto> GetClosingReportAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct = default);
}

public interface IReportExportService
{
    ReportFileResult ExportInventoryExcel(InventoryReportDto report);
    ReportFileResult ExportInventoryPdf(InventoryReportDto report);
    ReportFileResult ExportProductionExcel(ProductionReportDto report);
    ReportFileResult ExportProductionPdf(ProductionReportDto report);
    ReportFileResult ExportSalesExcel(SalesReportDto report);
    ReportFileResult ExportSalesPdf(SalesReportDto report);
    ReportFileResult ExportDeliveryExcel(DeliveryReportDto report);
    ReportFileResult ExportDeliveryPdf(DeliveryReportDto report);
    ReportFileResult ExportClosingExcel(ClosingReportDto report);
    ReportFileResult ExportClosingPdf(ClosingReportDto report);
}

public sealed class ReportingService(
    IAppDbContext db,
    ICurrentUserService currentUser,
    IRawMaterialService rawMaterialService,
    IFactoryInventoryService factoryInventoryService,
    IStoreInventoryService storeInventoryService,
    IProductionService productionService,
    IDeliveryService deliveryService,
    IDailyClosingService dailyClosingService,
    IWasteAndReturnService wasteService,
    ISystemSettingService settingService,
    IDateTimeProvider clock) : IReportingService
{
    public async Task<InventoryReportDto> GetInventoryReportAsync(CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var rawMaterials = (await rawMaterialService.GetPagedAsync(new RawMaterialQuery(PageSize: 200), ct)).Items;
        var factorySummary = await factoryInventoryService.GetProductSummaryAsync(ct: ct);
        var factoryLots = (await factoryInventoryService.GetLotsPagedAsync(new FactoryLotQuery(PageSize: 200), ct)).Items;
        var storeInventory = (await storeInventoryService.GetPagedAsync(new StoreInventoryQuery(PageSize: 200), ct)).Items;

        var alerts = new List<InventoryAlertItemDto>();
        alerts.AddRange(rawMaterials
            .Where(r => r.StockStatus is "Low Stock" or "Out of Stock")
            .Select(r => new InventoryAlertItemDto("Raw Material", r.Sku, r.Name, r.UnitCode, r.CurrentQuantity, r.MinStockLevel, r.StockStatus)));

        alerts.AddRange(factorySummary
            .Where(f => f.StockStatus is "Low Stock" or "Out of Stock")
            .Select(f => new InventoryAlertItemDto("Factory", f.ProductSku, f.ProductName, f.UnitCode, f.TotalAvailableQuantity, f.MinStockLevel, f.StockStatus)));

        alerts.AddRange(storeInventory
            .Where(s => s.StockStatus is "Low Stock" or "Out of Stock")
            .Select(s => new InventoryAlertItemDto("Store", s.ProductSku, s.ProductName, s.UnitCode, s.CurrentQuantity, s.MinStockLevel, s.StockStatus)));

        return new InventoryReportDto(clock.UtcNow, rawMaterials, factorySummary, factoryLots, storeInventory, alerts);
    }

    public async Task<ProductionReportDto> GetProductionReportAsync(DateOnly fromDate, DateOnly toDate, int? productId = null, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var paged = await productionService.GetPagedAsync(
            new ProductionQuery(productId, ProductionBatchStatus.Completed, fromDate, toDate, 1, 200), ct);
        var batches = paged.Items;

        var productSummaries = batches
            .GroupBy(b => new { b.ProductId, b.ProductSku, b.ProductName, b.OutputUnitCode })
            .Select(g => new ProductProductionSummaryRow(
                g.Key.ProductId,
                g.Key.ProductSku,
                g.Key.ProductName,
                g.Key.OutputUnitCode,
                g.Count(),
                g.Sum(x => x.PlannedQuantity),
                g.Sum(x => x.ActualQuantity)))
            .OrderByDescending(x => x.TotalActualQuantity)
            .ToList();

        var materialIds = batches.SelectMany(b => b.Consumptions).Select(c => c.RawMaterialId).Distinct().ToList();
        var materials = materialIds.Count == 0
            ? new Dictionary<int, (string Sku, string Name, string UnitCode)>()
            : (await db.RawMaterials.AsNoTracking()
                .Where(m => materialIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Sku, m.Name, UnitCode = m.Unit.Code })
                .ToListAsync(ct))
                .ToDictionary(m => m.Id, m => (m.Sku, m.Name, m.UnitCode));

        var ingredientSummaries = batches
            .SelectMany(b => b.Consumptions)
            .GroupBy(c => c.RawMaterialId)
            .Select(g =>
            {
                materials.TryGetValue(g.Key, out var mat);
                return new IngredientConsumptionSummaryRow(
                    g.Key,
                    mat.Sku ?? string.Empty,
                    mat.Name ?? g.First().SnapshotRawMaterialName,
                    mat.UnitCode ?? g.First().UnitCode,
                    g.Sum(x => x.PlannedQuantity),
                    g.Sum(x => x.ActualConsumedQuantity));
            })
            .OrderBy(x => x.RawMaterialName)
            .ToList();

        return new ProductionReportDto(
            fromDate,
            toDate,
            batches.Count,
            batches.Sum(b => b.ActualQuantity),
            productSummaries,
            ingredientSummaries,
            batches);
    }

    public async Task<SalesReportDto> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, int? productId = null, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var opConfig = await settingService.GetOperationalConfigAsync(ct);

        var txList = await db.PosTransactions.AsNoTracking()
            .Include(t => t.CashierUser)
            .Include(t => t.ApprovedByUser)
            .Include(t => t.Items)
            .Where(t => t.TransactionDate >= fromUtc && t.TransactionDate <= toUtc)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync(ct);

        if (productId.HasValue)
        {
            txList = txList.Where(t => t.Items.Any(i => i.ProductId == productId.Value)).ToList();
        }

        var completed = txList.Where(t => t.Status == PosTransactionStatus.Completed).ToList();
        var voided = txList.Where(t => t.Status == PosTransactionStatus.Voided).ToList();
        var refunded = txList.Where(t => t.Status == PosTransactionStatus.Refunded).ToList();

        var bestSelling = completed
            .SelectMany(t => t.Items)
            .Where(i => !productId.HasValue || i.ProductId == productId.Value)
            .GroupBy(i => new { i.ProductId, i.SkuSnapshot, i.ProductNameSnapshot })
            .Select(g => new BestSellingProductRow(
                g.Key.ProductId,
                g.Key.SkuSnapshot,
                g.Key.ProductNameSnapshot,
                g.Sum(x => x.Quantity),
                g.Sum(x => x.LineTotal)))
            .OrderByDescending(x => x.TotalQuantitySold)
            .ThenByDescending(x => x.GrossSalesRevenue)
            .ToList();

        var byCashier = completed
            .GroupBy(t => new { t.CashierUserId, t.CashierUser.FullName })
            .Select(g => new CashierSalesSummaryRow(
                g.Key.CashierUserId,
                g.Key.FullName,
                g.Count(),
                g.Sum(x => x.TotalAmount)))
            .OrderByDescending(x => x.NetSalesRevenue)
            .ToList();

        var reversedDtos = voided.Concat(refunded)
            .OrderByDescending(t => t.TransactionDate)
            .Select(t => new PosTransactionDto(
                t.Id,
                t.ReceiptNumber,
                t.ClientIdempotencyKey,
                t.CashierUserId,
                t.CashierUser.FullName,
                t.TransactionDate,
                t.SubTotal,
                t.DiscountAmount,
                t.TotalAmount,
                t.PaymentMethod,
                t.AmountTendered,
                t.ChangeAmount,
                t.Status,
                t.VoidOrRefundReason,
                t.ApprovedByUser?.FullName,
                t.ApprovedAt,
                t.Items.Select(i => new PosTransactionItemDto(
                    i.Id, i.ProductId, i.SkuSnapshot, i.ProductNameSnapshot, i.UnitPrice, i.Quantity, i.LineTotal)).ToList()))
            .ToList();

        return new SalesReportDto(
            fromUtc,
            toUtc,
            opConfig.CurrencySymbol,
            completed.Count,
            voided.Count,
            refunded.Count,
            completed.Sum(t => t.SubTotal),
            completed.Sum(t => t.DiscountAmount),
            completed.Sum(t => t.TotalAmount),
            voided.Sum(t => t.TotalAmount),
            refunded.Sum(t => t.TotalAmount),
            bestSelling,
            byCashier,
            reversedDtos);
    }

    public async Task<DeliveryReportDto> GetDeliveryReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var deliveries = (await deliveryService.GetPagedAsync(new DeliveryQuery(null, fromUtc, toUtc, 1, 200), ct)).Items;
        var discrepancies = deliveries
            .Where(d => d.Items.Any(i => i.DiscrepancyQuantity != 0m))
            .ToList();

        return new DeliveryReportDto(
            fromUtc,
            toUtc,
            deliveries.Count(d => d.Status is DeliveryStatus.Draft or DeliveryStatus.PendingDispatch),
            deliveries.Count(d => d.Status == DeliveryStatus.Dispatched),
            deliveries.Count(d => d.Status == DeliveryStatus.PartiallyReceived),
            deliveries.Count(d => d.Status == DeliveryStatus.Received),
            deliveries.Count(d => d.Status == DeliveryStatus.Cancelled),
            deliveries,
            discrepancies);
    }

    public async Task<ClosingReportDto> GetClosingReportAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
    {
        currentUser.EnsureInRole(AppRoles.Owner, AppRoles.Manager);

        var closings = (await dailyClosingService.GetHistoryAsync(fromDate, toDate, 1, 200, ct)).Items;
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = toDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var waste = (await wasteService.GetPagedAsync(new WasteAndReturnQuery(FromUtc: fromUtc, ToUtc: toUtc, PageSize: 200), ct)).Items;

        return new ClosingReportDto(fromDate, toDate, closings, waste);
    }
}
