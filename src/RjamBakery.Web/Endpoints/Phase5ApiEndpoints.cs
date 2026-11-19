using Microsoft.AspNetCore.Mvc;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Security;

namespace RjamBakery.Web.Endpoints;

public static class Phase5ApiEndpoints
{
    public static IEndpointRouteBuilder MapPhase5ApiEndpoints(this IEndpointRouteBuilder app)
    {
        var pos = app.MapGroup("/api/pos")
            .RequireAuthorization()
            .RequireRateLimiting(SecurityOptions.ApiRateLimitPolicy);

        pos.MapGet("/catalog", async (string? search, int? categoryId, IPosService service, CancellationToken ct)
            => Results.Ok(await service.GetPosCatalogAsync(search, categoryId, ct)))
            .RequireAuthorization(AppPolicies.OperatePos);

        pos.MapPost("/checkout", async ([FromBody] CompletePosSaleRequest request, IPosService service, CancellationToken ct)
            => Results.Ok(await service.CompleteSaleAsync(request, ct)))
            .RequireAuthorization(AppPolicies.OperatePos);

        pos.MapGet("/transactions", async (
            string? receiptNumber,
            PosTransactionStatus? status,
            string? cashierUserId,
            int? page,
            int? pageSize,
            IPosService service,
            CancellationToken ct)
            => Results.Ok(await service.GetTransactionsPagedAsync(
                new PosTransactionQuery(receiptNumber, status, cashierUserId, null, null, page ?? 1, pageSize ?? 25), ct)))
            .RequireAuthorization(AppPolicies.ViewStoreInventory);

        pos.MapGet("/transactions/{id:long}/receipt", async (long id, IPosService service, CancellationToken ct)
            => Results.Ok(await service.GetReceiptAsync(id, ct)))
            .RequireAuthorization(AppPolicies.ViewStoreInventory);

        pos.MapPost("/transactions/{id:long}/reverse", async (
            long id,
            [FromBody] ReversePosTransactionRequest request,
            IPosService service,
            CancellationToken ct)
            => Results.Ok(await service.ReverseTransactionAsync(id, request, ct)))
            .RequireAuthorization(AppPolicies.ApproveAdjustmentsAndVariances);

        return app;
    }
}
