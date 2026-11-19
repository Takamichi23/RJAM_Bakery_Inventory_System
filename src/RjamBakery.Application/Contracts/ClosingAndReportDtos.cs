using RjamBakery.Domain.Enums;

namespace RjamBakery.Application.Contracts;

// Module 10: Waste & Returns
public sealed record WasteAndReturnDto(
    long Id,
    string ReferenceNumber,
    InventoryLocation Location,
    int? ProductId,
    string? ProductName,
    int? RawMaterialId,
    string? RawMaterialName,
    long? FactoryStockLotId,
    string? LotNumber,
    long? PosTransactionId,
    string? ReceiptNumber,
    WasteReturnCategory Category,
    decimal Quantity,
    string Reason,
    string RecordedByUserName,
    DateTime RecordedAt,
    InspectionOutcome InspectionOutcome,
    ApprovalStatus ApprovalStatus,
    string? ApprovedByUserName,
    DateTime? ApprovedAt);

public sealed record RecordWasteOrReturnRequest(
    InventoryLocation Location,
    int? ProductId,
    int? RawMaterialId,
    long? FactoryStockLotId,
    long? PosTransactionId,
    WasteReturnCategory Category,
    decimal Quantity,
    string Reason);

public sealed record InspectCustomerReturnRequest(
    InspectionOutcome Outcome,
    string InspectionNotes);

public sealed record WasteAndReturnQuery(
    WasteReturnCategory? Category = null,
    InspectionOutcome? Outcome = null,
    InventoryLocation? Location = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int PageNumber = 1,
    int PageSize = 50);

// Module 11: Daily Closing & Carryover
public sealed record DailyClosingItemDto(
    long Id,
    int ProductId,
    string ProductSku,
    string ProductName,
    string UnitCode,
    decimal OpeningStock,
    decimal ConfirmedDeliveries,
    decimal ApprovedRestockedReturns,
    decimal TotalSold,
    decimal RecordedWaste,
    decimal ApprovedAdjustments,
    decimal ExpectedClosingStock,
    decimal ActualClosingStock,
    decimal VarianceQuantity,
    string VarianceReason,
    decimal CarryoverToNextDay);

public sealed record DailyClosingDto(
    long Id,
    string ClosingNumber,
    DateOnly OperatingDate,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    DailyClosingStatus Status,
    string? SubmittedByUserName,
    string? ApprovedByUserName,
    DateTime? ApprovedAt,
    string Notes,
    bool IsBeforeTargetClosingTime,
    IReadOnlyList<DailyClosingItemDto> Items);

public sealed record SubmitDailyClosingItemInput(
    int ProductId,
    decimal ActualClosingStock,
    string? VarianceReason = null);

public sealed record SubmitDailyClosingRequest(
    DateOnly OperatingDate,
    string Notes,
    IReadOnlyList<SubmitDailyClosingItemInput> Items);

public sealed record FinalizeDailyClosingRequest(
    bool ApproveVarianceAdjustments,
    string? ApprovalNotes = null);

// Module 12: Operational Reports
public sealed record InventoryAlertItemDto(
    string Bucket,
    string Sku,
    string Name,
    string UnitCode,
    decimal AvailableQuantity,
    decimal MinStockLevel,
    string StockStatus);

public sealed record InventoryReportDto(
    DateTime GeneratedAtUtc,
    IReadOnlyList<RawMaterialDto> RawMaterials,
    IReadOnlyList<FactoryProductSummaryDto> FactorySummary,
    IReadOnlyList<FactoryStockLotDto> FactoryLots,
    IReadOnlyList<StoreInventoryDto> StoreInventory,
    IReadOnlyList<InventoryAlertItemDto> LowAndOutOfStockItems);

public sealed record ProductProductionSummaryRow(
    int ProductId,
    string ProductSku,
    string ProductName,
    string UnitCode,
    int BatchCount,
    decimal TotalPlannedQuantity,
    decimal TotalActualQuantity);

public sealed record IngredientConsumptionSummaryRow(
    int RawMaterialId,
    string RawMaterialSku,
    string RawMaterialName,
    string UnitCode,
    decimal TotalPlannedConsumed,
    decimal TotalActualConsumed);

public sealed record ProductionReportDto(
    DateOnly FromDate,
    DateOnly ToDate,
    int CompletedBatches,
    decimal TotalUnitsProduced,
    IReadOnlyList<ProductProductionSummaryRow> ProductSummaries,
    IReadOnlyList<IngredientConsumptionSummaryRow> IngredientConsumptions,
    IReadOnlyList<ProductionBatchDto> Batches);

public sealed record BestSellingProductRow(
    int ProductId,
    string Sku,
    string ProductName,
    decimal TotalQuantitySold,
    decimal GrossSalesRevenue);

public sealed record CashierSalesSummaryRow(
    string CashierUserId,
    string CashierName,
    int CompletedTransactionCount,
    decimal NetSalesRevenue);

public sealed record SalesReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    string CurrencySymbol,
    int CompletedCount,
    int VoidedCount,
    int RefundedCount,
    decimal GrossSubTotal,
    decimal TotalDiscounts,
    decimal NetSalesRevenue,
    decimal VoidedAmount,
    decimal RefundedAmount,
    IReadOnlyList<BestSellingProductRow> BestSellingProducts,
    IReadOnlyList<CashierSalesSummaryRow> SalesByCashier,
    IReadOnlyList<PosTransactionDto> ReversedTransactions);

public sealed record DeliveryReportDto(
    DateTime FromUtc,
    DateTime ToUtc,
    int PendingCount,
    int DispatchedCount,
    int PartiallyReceivedCount,
    int ReceivedCount,
    int CancelledCount,
    IReadOnlyList<DeliveryDto> Deliveries,
    IReadOnlyList<DeliveryDto> DiscrepancyDeliveries);

public sealed record ClosingReportDto(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<DailyClosingDto> Closings,
    IReadOnlyList<WasteAndReturnDto> WasteAndReturns);

public sealed record ReportFileResult(
    byte[] Content,
    string ContentType,
    string FileName);
