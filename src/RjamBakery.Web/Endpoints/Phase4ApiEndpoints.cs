using Microsoft.AspNetCore.Mvc;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Security;

namespace RjamBakery.Web.Endpoints;

public static class Phase4ApiEndpoints
{
    public static IEndpointRouteBuilder MapPhase4ApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireRateLimiting(SecurityOptions.ApiRateLimitPolicy);

        // Store Deliveries
        var deliveries = api.MapGroup("/deliveries").RequireAuthorization();
        deliveries.MapGet("/", async (
            DeliveryStatus? status,
            int? page,
            int? pageSize,
            IDeliveryService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(new DeliveryQuery(status, null, null, page ?? 1, pageSize ?? 25), ct)));

        deliveries.MapGet("/{id:long}", async (long id, IDeliveryService service, CancellationToken ct)
            => Results.Ok(await service.GetByIdAsync(id, ct)));

        deliveries.MapPost("/", async ([FromBody] CreateDeliveryRequest request, IDeliveryService service, CancellationToken ct)
            => Results.Ok(await service.CreateDraftAsync(request, ct)))
            .RequireAuthorization(AppPolicies.DispatchDeliveries);

        deliveries.MapPost("/{id:long}/prepare", async (long id, IDeliveryService service, CancellationToken ct)
            => Results.Ok(await service.PrepareForDispatchAsync(id, ct)))
            .RequireAuthorization(AppPolicies.DispatchDeliveries);

        deliveries.MapPost("/{id:long}/dispatch", async (long id, HttpContext http, IDeliveryService service, CancellationToken ct) =>
        {
            DispatchDeliveryRequest? request = null;
            if (http.Request.ContentLength is > 0)
            {
                request = await http.Request.ReadFromJsonAsync<DispatchDeliveryRequest>(ct);
            }
            return Results.Ok(await service.DispatchAsync(id, request, ct));
        }).RequireAuthorization(AppPolicies.DispatchDeliveries);

        deliveries.MapPost("/{id:long}/cancel", async (long id, [FromBody] ReasonRequest request, IDeliveryService service, CancellationToken ct) =>
        {
            await service.CancelAsync(id, request.Reason, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.DispatchDeliveries);

        deliveries.MapPost("/{id:long}/receive", async (long id, [FromBody] ConfirmDeliveryReceivingRequest request, IDeliveryService service, CancellationToken ct)
            => Results.Ok(await service.ConfirmReceivingAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ReceiveDeliveries);

        deliveries.MapPost("/{id:long}/reconcile", async (long id, [FromBody] ReconcileDeliveryRequest request, IDeliveryService service, CancellationToken ct)
            => Results.Ok(await service.ReconcileDiscrepancyAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        // Store Inventory & Adjustments
        var store = api.MapGroup("/store-inventory").RequireAuthorization();
        store.MapGet("/", async (
            string? search,
            int? categoryId,
            bool? lowStockOnly,
            bool? outOfStockOnly,
            int? page,
            int? pageSize,
            IStoreInventoryService service,
            CancellationToken ct)
            => Results.Ok(await service.GetPagedAsync(
                new StoreInventoryQuery(search, categoryId, lowStockOnly ?? false, outOfStockOnly ?? false, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ViewStoreInventory);

        store.MapGet("/{productId:int}", async (int productId, IStoreInventoryService service, CancellationToken ct)
            => Results.Ok(await service.GetByProductIdAsync(productId, ct)))
            .RequireAuthorization(AppPolicies.ViewStoreInventory);

        store.MapPut("/threshold", async ([FromBody] UpdateStoreThresholdRequest request, IStoreInventoryService service, CancellationToken ct) =>
        {
            await service.UpdateThresholdAsync(request, ct);
            return Results.NoContent();
        }).RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        store.MapGet("/adjustments", async (
            ApprovalStatus? status,
            int? page,
            int? pageSize,
            IStoreInventoryService service,
            CancellationToken ct)
            => Results.Ok(await service.GetAdjustmentRequestsPagedAsync(status, page ?? 1, pageSize ?? 25, ct)));

        store.MapPost("/adjustments/request", async ([FromBody] CreateStockAdjustmentRequest request, IStoreInventoryService service, CancellationToken ct)
            => Results.Ok(await service.RequestAdjustmentAsync(request, ct)));

        store.MapPost("/adjustments/{id:long}/review", async (long id, [FromBody] ReviewStockAdjustmentRequest request, IStoreInventoryService service, CancellationToken ct)
            => Results.Ok(await service.ReviewAdjustmentAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        store.MapPost("/adjustments/direct", async ([FromBody] DirectStoreAdjustmentRequest request, IStoreInventoryService service, CancellationToken ct)
            => Results.Ok(await service.ApplyDirectStoreAdjustmentAsync(request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        return app;
    }
}
