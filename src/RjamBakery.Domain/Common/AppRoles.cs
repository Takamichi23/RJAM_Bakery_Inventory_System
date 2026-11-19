namespace RjamBakery.Domain.Common;

/// <summary>
/// Canonical application roles defined in Part 3 of the RJAM Bakery specification.
/// </summary>
public static class AppRoles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Baker = "Baker";
    public const string Cashier = "Cashier";

    public static readonly IReadOnlyList<string> All =
    [
        Owner,
        Manager,
        Baker,
        Cashier
    ];
}

/// <summary>
/// Named authorization policies enforced identically at both UI and backend layers.
/// </summary>
public static class AppPolicies
{
    public const string OwnerOnly = "Policy.OwnerOnly";
    public const string ManagementOrOwner = "Policy.ManagementOrOwner";
    public const string ManageCatalog = "Policy.ManageCatalog";
    public const string ViewCatalog = "Policy.ViewCatalog";
    public const string ManageSuppliers = "Policy.ManageSuppliers";
    public const string ViewSuppliers = "Policy.ViewSuppliers";
    public const string ManageRawMaterials = "Policy.ManageRawMaterials";
    public const string ViewRawMaterials = "Policy.ViewRawMaterials";
    public const string ReceiveRawMaterials = "Policy.ReceiveRawMaterials";
    public const string ManageRecipes = "Policy.ManageRecipes";
    public const string OperateProduction = "Policy.OperateProduction";
    public const string ViewFactoryInventory = "Policy.ViewFactoryInventory";
    public const string DispatchDeliveries = "Policy.DispatchDeliveries";
    public const string ReceiveDeliveries = "Policy.ReceiveDeliveries";
    public const string ViewStoreInventory = "Policy.ViewStoreInventory";
    public const string OperatePos = "Policy.OperatePos";
    public const string RecordWasteOrReturn = "Policy.RecordWasteOrReturn";
    public const string ApproveAdjustmentsAndVariances = "Policy.ApproveAdjustmentsAndVariances";
    public const string SubmitDailyClosing = "Policy.SubmitDailyClosing";
    public const string ViewAuditLogs = "Policy.ViewAuditLogs";
    public const string ViewReports = "Policy.ViewReports";
    public const string ManageUsers = "Policy.ManageUsers";
    public const string ManageSettings = "Policy.ManageSettings";
}
