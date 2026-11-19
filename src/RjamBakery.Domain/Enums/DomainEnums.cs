namespace RjamBakery.Domain.Enums;

public enum UnitDimension
{
    Mass = 1,
    Volume = 2,
    Count = 3
}

public enum SupplierStatus
{
    Active = 1,
    Inactive = 2
}

public enum MaterialReceiptStatus
{
    Draft = 1,
    Confirmed = 2,
    Cancelled = 3
}

public enum ProductionBatchStatus
{
    Planned = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4
}

public enum FactoryLotStatus
{
    Available = 1,
    Depleted = 2,
    Expired = 3,
    Quarantined = 4
}

public enum DeliveryStatus
{
    Draft = 1,
    PendingDispatch = 2,
    Dispatched = 3,
    PartiallyReceived = 4,
    Received = 5,
    Cancelled = 6
}

public enum ReconciliationStatus
{
    None = 0,
    PendingReview = 1,
    Resolved = 2
}

public enum PaymentMethod
{
    Cash = 1,
    GCash = 2,
    Card = 3,
    BankTransfer = 4,
    Other = 5
}

public enum PosTransactionStatus
{
    Completed = 1,
    Voided = 2,
    Refunded = 3
}

public enum WasteReturnCategory
{
    Damaged = 1,
    Spoiled = 2,
    Expired = 3,
    CustomerReturn = 4,
    OtherLoss = 5
}

public enum InspectionOutcome
{
    NotApplicable = 0,
    PendingInspection = 1,
    RestockedToSellable = 2,
    DisposedAsLoss = 3
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3
}

public enum InventoryLocation
{
    Supplier = 1,
    Factory = 2,
    InTransit = 3,
    Store = 4,
    ExternalCustomer = 5,
    WasteDisposal = 6,
    Adjustment = 7
}

public enum AdjustmentLocation
{
    RawMaterial = 1,
    Factory = 2,
    Store = 3
}

public enum DailyClosingStatus
{
    Open = 1,
    Submitted = 2,
    VarianceReviewPending = 3,
    Finalized = 4
}

public enum InventoryItemType
{
    RawMaterial = 1,
    FinishedProduct = 2
}

public enum InventoryMovementType
{
    OpeningStock = 1,
    SupplierReceiving = 2,
    ProductionConsumption = 3,
    ProductionOutput = 4,
    FactoryDispatch = 5,
    StoreReceiving = 6,
    PosSale = 7,
    CustomerReturn = 8,
    DamagedProduct = 9,
    SpoiledProduct = 10,
    ExpiredProduct = 11,
    StockAdjustment = 12,
    TransactionReversal = 13,
    DeliveryVarianceWriteOff = 14
}
