using Microsoft.AspNetCore.Mvc;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Security;

namespace RjamBakery.Web.Endpoints;

public sealed record SupplierStatusRequest(SupplierStatus Status);
public sealed record ReasonRequest(string Reason);
public sealed record LotStatusUpdateRequest(FactoryLotStatus TargetStatus, string Reason);

public static class Phase3ApiEndpoints
{
    public static IEndpointRouteBuilder MapPhase3ApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireRateLimiting(SecurityOptions.ApiRateLimitPolicy);

        // Suppliers
        var suppliers = api.MapGroup("/suppliers").RequireAuthorization();
        suppliers.MapGet("/", async (
            string? search,
            bool? includeInactive,
            int? page,
            int? pageSize,
            ISupplierService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(search, includeInactive ?? false, page ?? 1, pageSize ?? 25, ct)))
            .RequireAuthorization(AppPolicies.ViewSuppliers);

        suppliers.MapGet("/{id:int}", async (int id, ISupplierService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ViewSuppliers);

        suppliers.MapPost("/", async ([FromBody] SaveSupplierRequest request, ISupplierService service, CancellationToken ct)
            => Results.Ok(await service.SaveAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageSuppliers);

        suppliers.MapPatch("/{id:int}/status", async (int id, [FromBody] SupplierStatusRequest request, ISupplierService service, CancellationToken ct) =>
        {
            await service.SetStatusAsync(id, request.Status, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ManageSuppliers);

        // Material Receiving
        var receipts = api.MapGroup("/material-receipts").RequireAuthorization();
        receipts.MapGet("/", async (
            int? supplierId,
            MaterialReceiptStatus? status,
            int? page,
            int? pageSize,
            IMaterialReceiptService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new MaterialReceiptQuery(supplierId, status, null, null, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ReceiveRawMaterials);

        receipts.MapGet("/{id:long}", async (long id, IMaterialReceiptService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ReceiveRawMaterials);

        receipts.MapPost("/", async ([FromBody] CreateMaterialReceiptRequest request, IMaterialReceiptService service, CancellationToken ct)
            => Results.Ok(await service.CreateDraftAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ReceiveRawMaterials);

        receipts.MapPost("/{id:long}/confirm", async (long id, IMaterialReceiptService service, CancellationToken ct)
            => Results.Ok(await service.ConfirmAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ReceiveRawMaterials);

        receipts.MapPost("/{id:long}/cancel", async (long id, [FromBody] ReasonRequest request, IMaterialReceiptService service, CancellationToken ct) =>
        {
            await service.CancelDraftAsync(id, request.Reason, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ReceiveRawMaterials);

        // Recipes (BOM)
        var recipes = api.MapGroup("/recipes").RequireAuthorization();
        recipes.MapGet("/product/{productId:int}/active", async (int productId, IRecipeService service, CancellationToken ct) =>
        {
            var recipe = await service.GetActiveByProductAsync(productId, ct);
            return recipe is null ? Results.NotFound() : Results.Ok(recipe);
        }).RequireAuthorization(AppPolicies.ViewRawMaterials);

        recipes.MapGet("/product/{productId:int}/history", async (int productId, IRecipeService service, CancellationToken ct)
            => Results.Ok(await service.GetHistoryByProductAsync(productId, ct)))
            .RequireAuthorization(AppPolicies.ViewRawMaterials);

        recipes.MapPost("/", async ([FromBody] SaveRecipeRequest request, IRecipeService service, CancellationToken ct)
            => Results.Ok(await service.SaveNewVersionAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ManageRecipes);

        recipes.MapGet("/product/{productId:int}/calculate", async (
            int productId,
            decimal targetQuantity,
            IRecipeService service,
            CancellationToken ct)
            => Results.Ok(await service.CalculateRequirementsAsync(productId, targetQuantity, ct)))
            .RequireAuthorization(AppPolicies.ViewRawMaterials);

        // Bakery Production
        var production = api.MapGroup("/production").RequireAuthorization();
        production.MapGet("/", async (
            int? productId,
            ProductionBatchStatus? status,
            int? page,
            int? pageSize,
            IProductionService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new ProductionQuery(productId, status, null, null, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ViewFactoryInventory);

        production.MapGet("/{id:long}", async (long id, IProductionService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ViewFactoryInventory);

        production.MapPost("/plan", async ([FromBody] CreateProductionPlanRequest request, IProductionService service, CancellationToken ct)
            => Results.Ok(await service.CreatePlanAsync(request, ct)))
            .RequireAuthorization(AppPolicies.OperateProduction);

        production.MapPost("/finalize", async ([FromBody] FinalizeProductionRequest request, IProductionService service, CancellationToken ct)
            => Results.Ok(await service.FinalizeProductionAsync(request, ct)))
            .RequireAuthorization(AppPolicies.OperateProduction);

        production.MapPost("/{id:long}/cancel", async (long id, [FromBody] ReasonRequest request, IProductionService service, CancellationToken ct) =>
        {
            await service.CancelPlanAsync(id, request.Reason, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.OperateProduction);

        // Factory Finished Goods Inventory
        var factoryInv = api.MapGroup("/factory-inventory").RequireAuthorization();
        factoryInv.MapGet("/lots", async (
            int? productId,
            FactoryLotStatus? status,
            bool? onlyAvailable,
            int? page,
            int? pageSize,
            IFactoryInventoryService service,
            CancellationToken ct)
            => Results.Ok(await service.GetLotsPagedAsync(
                new FactoryLotQuery(productId, status, onlyAvailable ?? false, page ?? 1, pageSize ?? 50), ct)))
            .RequireAuthorization(AppPolicies.ViewFactoryInventory);

        factoryInv.MapGet("/summary", async (string? search, IFactoryInventoryService service, CancellationToken ct)
            => Results.Ok(await service.GetProductSummaryAsync(search, ct)))
            .RequireAuthorization(AppPolicies.ViewFactoryInventory);

        factoryInv.MapGet("/fefo-preview", async (int productId, decimal quantity, IFactoryInventoryService service, CancellationToken ct)
            => Results.Ok(await service.PreviewFefoAllocationAsync(productId, quantity, ct)))
            .RequireAuthorization(AppPolicies.ViewFactoryInventory);

        factoryInv.MapPost("/lots/{lotId:long}/status", async (
            long lotId,
            [FromBody] LotStatusUpdateRequest request,
            IFactoryInventoryService service,
            CancellationToken ct) =>
        {
            await service.MarkLotExpiredOrQuarantinedAsync(lotId, request.TargetStatus, request.Reason, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        return app;
    }
}
