namespace RjamBakery.Application.Contracts;

public sealed record DashboardAlertDto(
    string Severity,
    string Title,
    string Detail,
    string TargetUrl);

public sealed record RecentActivityDto(
    DateTime OccurredAt,
    string MovementNumber,
    string ItemName,
    string MovementType,
    decimal QuantityDelta,
    string UnitCode,
    string ReferenceNumber,
    string ResponsibleUserName);

public sealed record DashboardSummaryDto(
    string ActiveRole,
    string BakeryName,
    string CurrencySymbol,
    int TotalActiveProducts,
    decimal TotalRawMaterialUnits,
    decimal TotalFactoryFinishedUnits,
    decimal TotalStoreSellableUnits,
    int LowStockCount,
    int OutOfStockCount,
    decimal TodayProductionUnits,
    int TodayProductionBatches,
    int PendingDeliveriesCount,
    int DispatchedDeliveriesCount,
    decimal TodayNetSales,
    int TodayTransactionCount,
    int OpenDiscrepanciesCount,
    int PendingReturnsInspectionCount,
    int PendingAdjustmentsCount,
    int PendingClosingVariancesCount,
    IReadOnlyList<DashboardAlertDto> Alerts,
    IReadOnlyList<RecentActivityDto> RecentActivities);
