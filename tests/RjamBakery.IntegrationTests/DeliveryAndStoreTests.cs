using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;
using RjamBakery.Infrastructure.Persistence;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class DeliveryAndStoreTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task FullFactoryToStoreWorkflow_ReservesFefoLotsDispatchesAndIncreasesStoreOnlyOnConfirm()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var ensaymada = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P4-ENS-01", "P4 Ensaymada", "Sweet pastry", pcsUnit.Id, 25m, 15m, 3, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        // Produce 2 batches of 60 pcs each (total 120 pcs)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, ensaymada!.Id, 60m, 60m, today.AddDays(-1), today.AddDays(2), "Lot 1", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();
        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, ensaymada.Id, 60m, 60m, today, today.AddDays(4), "Lot 2", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();

        // Step 1: Create Draft Delivery for 100 pcs
        var draftResp = await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver Manny", "Morning dispatch", [new DeliveryItemInput(ensaymada.Id, 100m)]));
        draftResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var draft = await draftResp.Content.ReadFromJsonAsync<DeliveryDto>();
        draft!.Status.Should().Be(DeliveryStatus.Draft);

        // Store inventory MUST still be 0!
        var storeBefore = await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{ensaymada.Id}");
        storeBefore!.CurrentQuantity.Should().Be(0m);

        // Step 2: Prepare Delivery -> allocates FEFO (60 from Lot 1 + 40 from Lot 2) and reserves
        var prepResp = await baker.PostAsync($"/api/deliveries/{draft.Id}/prepare", null);
        prepResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var prepared = await prepResp.Content.ReadFromJsonAsync<DeliveryDto>();
        prepared!.Status.Should().Be(DeliveryStatus.PendingDispatch);
        prepared.Items.Should().HaveCount(2);
        prepared.Items.Sum(i => i.RequestedQuantity).Should().Be(100m);

        // Only 20 unreserved remain -> requesting 30 more must fail
        var overReserveResp = await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver 2", "Too much", [new DeliveryItemInput(ensaymada.Id, 30m)]));
        overReserveResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Step 3: Dispatch Delivery -> Factory stock decreases, reservation clears, Store STILL 0
        var dispatchResp = await baker.PostAsync($"/api/deliveries/{draft.Id}/dispatch", null);
        dispatchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dispatched = await dispatchResp.Content.ReadFromJsonAsync<DeliveryDto>();
        dispatched!.Status.Should().Be(DeliveryStatus.Dispatched);

        var factorySummary = await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>("/api/factory-inventory/summary?search=P4-ENS-01");
        factorySummary!.Single().TotalRemainingQuantity.Should().Be(20.0000m);
        factorySummary!.Single().TotalReservedQuantity.Should().Be(0m);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{ensaymada.Id}"))!.CurrentQuantity.Should().Be(0m);

        // Step 4: Cashier Confirms Receiving of all 100 pcs -> Store increases to 100
        var receiveResp = await cashier.PostAsJsonAsync($"/api/deliveries/{draft.Id}/receive",
            new ConfirmDeliveryReceivingRequest("Received intact",
                dispatched.Items.Select(i => new ReceiveDeliveryItemInput(i.Id, i.DispatchedQuantity)).ToList()));
        receiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var received = await receiveResp.Content.ReadFromJsonAsync<DeliveryDto>();
        received!.Status.Should().Be(DeliveryStatus.Received);

        var storeAfter = await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{ensaymada.Id}");
        storeAfter!.CurrentQuantity.Should().Be(100.0000m);
        storeAfter.LastReceivedAt.Should().NotBeNull();

        // Duplicate receiving must return 409 Conflict
        var dupReceive = await cashier.PostAsJsonAsync($"/api/deliveries/{draft.Id}/receive",
            new ConfirmDeliveryReceivingRequest("Retry",
                dispatched.Items.Select(i => new ReceiveDeliveryItemInput(i.Id, i.DispatchedQuantity)).ToList()));
        dupReceive.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Ledger integrity must be 100% balanced
        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
        verify.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task DeliveryDiscrepancyAndCancelReservation_ReconcileCleanly()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var brioche = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P4-BRI-01", "P4 Brioche", "Test", pcsUnit.Id, 45m, 10m, 3, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, brioche!.Id, 50m, 50m, DateOnly.FromDateTime(DateTime.UtcNow), null, "Brioche batch", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();

        // Test prepare -> cancel releases reservation
        var delCancel = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver", "Will cancel", [new DeliveryItemInput(brioche.Id, 20m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        (await baker.PostAsync($"/api/deliveries/{delCancel!.Id}/prepare", null)).EnsureSuccessStatusCode();
        (await baker.PostAsJsonAsync($"/api/deliveries/{delCancel.Id}/cancel", new { Reason = "Schedule changed" })).EnsureSuccessStatusCode();

        var afterCancelSummary = await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>("/api/factory-inventory/summary?search=P4-BRI-01");
        afterCancelSummary!.Single().TotalAvailableQuantity.Should().Be(50.0000m);

        // Dispatch all 50 pcs
        var delReal = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver 1", "Dispatched 50", [new DeliveryItemInput(brioche.Id, 50m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        var disp = await (await baker.PostAsync($"/api/deliveries/{delReal!.Id}/dispatch", null)).Content.ReadFromJsonAsync<DeliveryDto>();

        // Cashier receives only 45 (5 missing)
        var recResp = await cashier.PostAsJsonAsync($"/api/deliveries/{delReal.Id}/receive",
            new ConfirmDeliveryReceivingRequest("Short 5 units",
                [new ReceiveDeliveryItemInput(disp!.Items.Single().Id, 45m, "5 pcs damaged/missing in van")]));
        recResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var partial = await recResp.Content.ReadFromJsonAsync<DeliveryDto>();
        partial!.Status.Should().Be(DeliveryStatus.PartiallyReceived);
        partial.Items.Single().DiscrepancyQuantity.Should().Be(5.0000m);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{brioche.Id}"))!.CurrentQuantity.Should().Be(45.0000m);

        // Manager reconciles the 5 pcs as ReturnToFactoryLot -> Factory lot regains 5 pcs and Delivery becomes Received
        var reconcileResp = await manager.PostAsJsonAsync($"/api/deliveries/{delReal.Id}/reconcile",
            new ReconcileDeliveryRequest(
                [new ReconcileDeliveryItemInput(partial.Items.Single().Id, DiscrepancyResolutionType.ReturnToFactoryLot, "Driver returned 5 unsold boxes to factory")]));
        reconcileResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reconciled = await reconcileResp.Content.ReadFromJsonAsync<DeliveryDto>();
        reconciled!.Status.Should().Be(DeliveryStatus.Received);

        var factoryAfterReturn = await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>("/api/factory-inventory/summary?search=P4-BRI-01");
        factoryAfterReturn!.Single().TotalRemainingQuantity.Should().Be(5.0000m);

        // Also test TransitLossWriteOff on a 2nd delivery of those remaining 5 pcs
        var del2 = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver 1", "Second trip", [new DeliveryItemInput(brioche.Id, 5m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        var disp2 = await (await baker.PostAsync($"/api/deliveries/{del2!.Id}/dispatch", null)).Content.ReadFromJsonAsync<DeliveryDto>();
        var part2 = await (await cashier.PostAsJsonAsync($"/api/deliveries/{del2.Id}/receive",
            new ConfirmDeliveryReceivingRequest("2 dropped",
                [new ReceiveDeliveryItemInput(disp2!.Items.Single().Id, 3m, "2 fell off tray")]))).Content.ReadFromJsonAsync<DeliveryDto>();

        var rec2 = await manager.PostAsJsonAsync($"/api/deliveries/{del2.Id}/reconcile",
            new ReconcileDeliveryRequest(
                [new ReconcileDeliveryItemInput(part2!.Items.Single().Id, DiscrepancyResolutionType.TransitLossWriteOff, "Confirmed transit spoilage")]));
        rec2.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transitWaste = await db.WasteAndReturnRecords.AsNoTracking()
                .FirstOrDefaultAsync(w => w.ProductId == brioche.Id && w.Location == InventoryLocation.InTransit);
            transitWaste.Should().NotBeNull();
            transitWaste!.Quantity.Should().Be(2.0000m);
        }

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task StoreAdjustmentsAndPhase4Rbac_RequireManagerOrOwnerApproval()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var cookie = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P4-CKY-01", "P4 Cookie", "Test", pcsUnit.Id, 18m, 10m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        // Direct store adjustment blocked for Cashier & Baker
        (await cashier.PostAsJsonAsync("/api/store-inventory/adjustments/direct",
            new DirectStoreAdjustmentRequest(cookie!.Id, 10m, "Cashier direct try"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.GetAsync("/api/store-inventory")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.PostAsJsonAsync($"/api/deliveries/1/receive",
            new ConfirmDeliveryReceivingRequest(null, [new ReceiveDeliveryItemInput(1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Cashier requests Store adjustment (+12 opening count) -> stays Pending, store stock stays 0
        var reqResp = await cashier.PostAsJsonAsync("/api/store-inventory/adjustments/request",
            new CreateStockAdjustmentRequest(AdjustmentLocation.Store, cookie.Id, null, null, 12m, "Found unrecorded opening tray"));
        reqResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var adjReq = await reqResp.Content.ReadFromJsonAsync<StockAdjustmentRequestDto>();
        adjReq!.ApprovalStatus.Should().Be(ApprovalStatus.Pending);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{cookie.Id}"))!.CurrentQuantity.Should().Be(0m);

        // Cashier cannot self-approve
        (await cashier.PostAsJsonAsync($"/api/store-inventory/adjustments/{adjReq.Id}/review",
            new ReviewStockAdjustmentRequest(true, "Self approve"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager approves -> Store stock increases to 12
        var approveResp = await manager.PostAsJsonAsync($"/api/store-inventory/adjustments/{adjReq.Id}/review",
            new ReviewStockAdjustmentRequest(true, "Verified physical count"));
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approveResp.Content.ReadFromJsonAsync<StockAdjustmentRequestDto>())!.ApprovalStatus.Should().Be(ApprovalStatus.Approved);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{cookie.Id}"))!.CurrentQuantity.Should().Be(12.0000m);

        // Re-approving returns 409 Conflict
        (await manager.PostAsJsonAsync($"/api/store-inventory/adjustments/{adjReq.Id}/review",
            new ReviewStockAdjustmentRequest(true, "Retry"))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
    }
}
