using RjamBakery.Domain.Enums;

namespace RjamBakery.Domain.Entities;

public class PosTransaction
{
    public long Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public string ClientIdempotencyKey { get; set; } = string.Empty;
    public string CashierUserId { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public decimal AmountTendered { get; set; }
    public decimal ChangeAmount { get; set; }
    public PosTransactionStatus Status { get; set; } = PosTransactionStatus.Completed;
    public string? VoidOrRefundReason { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public ApplicationUser CashierUser { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public ICollection<PosTransactionItem> Items { get; set; } = [];
}

public class PosTransactionItem
{
    public long Id { get; set; }
    public long PosTransactionId { get; set; }
    public int ProductId { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string SkuSnapshot { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal LineTotal { get; set; }

    public PosTransaction PosTransaction { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

public class WasteAndReturnRecord
{
    public long Id { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public InventoryLocation Location { get; set; } = InventoryLocation.Store;
    public int? ProductId { get; set; }
    public int? RawMaterialId { get; set; }
    public long? FactoryStockLotId { get; set; }
    public long? PosTransactionId { get; set; }
    public WasteReturnCategory Category { get; set; }
    public decimal Quantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RecordedByUserId { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public InspectionOutcome InspectionOutcome { get; set; } = InspectionOutcome.NotApplicable;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public Product? Product { get; set; }
    public RawMaterial? RawMaterial { get; set; }
    public FactoryStockLot? FactoryStockLot { get; set; }
    public PosTransaction? PosTransaction { get; set; }
    public ApplicationUser RecordedByUser { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
}

public class StockAdjustmentRequest
{
    public long Id { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public AdjustmentLocation Location { get; set; }
    public int? ProductId { get; set; }
    public int? RawMaterialId { get; set; }
    public long? FactoryStockLotId { get; set; }
    public decimal QuantityDelta { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestedByUserId { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public long? DailyClosingId { get; set; }

    public Product? Product { get; set; }
    public RawMaterial? RawMaterial { get; set; }
    public FactoryStockLot? FactoryStockLot { get; set; }
    public ApplicationUser RequestedByUser { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public DailyClosing? DailyClosing { get; set; }
}

public class DailyClosing
{
    public long Id { get; set; }
    public string ClosingNumber { get; set; } = string.Empty;
    public DateOnly OperatingDate { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public DailyClosingStatus Status { get; set; } = DailyClosingStatus.Open;
    public string? SubmittedByUserId { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string Notes { get; set; } = string.Empty;

    public ApplicationUser? SubmittedByUser { get; set; }
    public ApplicationUser? ApprovedByUser { get; set; }
    public ICollection<DailyClosingItem> Items { get; set; } = [];
    public ICollection<StockAdjustmentRequest> VarianceAdjustments { get; set; } = [];
}

public class DailyClosingItem
{
    public long Id { get; set; }
    public long DailyClosingId { get; set; }
    public int ProductId { get; set; }
    public decimal OpeningStock { get; set; }
    public decimal ConfirmedDeliveries { get; set; }
    public decimal ApprovedRestockedReturns { get; set; }
    public decimal TotalSold { get; set; }
    public decimal RecordedWaste { get; set; }
    public decimal ApprovedAdjustments { get; set; }
    public decimal ExpectedClosingStock { get; set; }
    public decimal ActualClosingStock { get; set; }
    public decimal VarianceQuantity { get; set; }
    public string VarianceReason { get; set; } = string.Empty;
    public decimal CarryoverToNextDay { get; set; }

    public DailyClosing DailyClosing { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

public class InventoryMovement
{
    public long Id { get; set; }
    public string MovementNumber { get; set; } = string.Empty;
    /// <summary>
    /// Database-enforced unique key preventing duplicate ledger postings under concurrent or retried transactions.
    /// </summary>
    public string DeduplicationKey { get; set; } = string.Empty;
    public InventoryItemType ItemType { get; set; }
    public int? RawMaterialId { get; set; }
    public int? ProductId { get; set; }
    public long? FactoryStockLotId { get; set; }
    public InventoryLocation SourceLocation { get; set; }
    public InventoryLocation DestinationLocation { get; set; }
    public InventoryMovementType MovementType { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal AbsoluteQuantity { get; set; }
    public int UnitId { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal NewQuantity { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string ResponsibleUserId { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;

    public RawMaterial? RawMaterial { get; set; }
    public Product? Product { get; set; }
    public FactoryStockLot? FactoryStockLot { get; set; }
    public UnitOfMeasure Unit { get; set; } = null!;
    public ApplicationUser ResponsibleUser { get; set; } = null!;
}
