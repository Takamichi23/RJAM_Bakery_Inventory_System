using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class EndToEndBakeryScenarioTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task Part9_NormalBakeryOperatingDay_CompleteEndToEndScenario()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var kgUnit = units!.Single(u => u.Code == "kg");
        var gUnit = units!.Single(u => u.Code == "g");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        var breadCategory = categories!.First(c => c.Name == "Bread");

        // Configurable test catalog & materials (not hardcoded recipes)
        var pandesal = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, breadCategory.Id, "E2E-PND-01", "E2E Pandesal", "Warm breakfast roll", pcsUnit.Id, 3.00m, 30m, 2, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        var flour = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "E2E-RM-FLR", "E2E Bread Flour", "", kgUnit.Id, null, 10m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();
        var sugar = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "E2E-RM-SGR", "E2E Refined Sugar", "", kgUnit.Id, null, 5m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();

        var supplier = await (await owner.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(
            null, "E2E Grain Traders", "Maria Santos", "09170001111", "maria@traders.test", "Quezon City", SupplierStatus.Active, "E2E supplier",
            [
                new SupplierMaterialLinkRequest(flour!.Id, 950m, 1),
                new SupplierMaterialLinkRequest(sugar!.Id, 60m, 1)
            ]))).Content.ReadFromJsonAsync<SupplierDto>();

        // Configurable recipe: 100 pcs yield => 2.5 kg Flour + 250 g Sugar
        (await owner.PostAsJsonAsync("/api/recipes", new SaveRecipeRequest(
            pandesal!.Id, 100m, pcsUnit.Id, "E2E configurable test recipe",
            [
                new RecipeIngredientInput(flour.Id, 2.5m, kgUnit.Id),
                new RecipeIngredientInput(sugar!.Id, 250m, gUnit.Id)
            ]))).EnsureSuccessStatusCode();

        // Steps 1-2: Supplier delivers Flour (50 kg) and Sugar (20 kg); factory staff receive and confirm
        var receiptDraft = await (await baker.PostAsJsonAsync("/api/material-receipts", new CreateMaterialReceiptRequest(
            supplier!.Id, DateTime.UtcNow, "Morning raw material delivery",
            [
                new MaterialReceiptItemInput(flour.Id, 50m, kgUnit.Id, 45m),
                new MaterialReceiptItemInput(sugar.Id, 20m, kgUnit.Id, 60m)
            ]))).Content.ReadFromJsonAsync<MaterialReceiptDto>();
        (await baker.PostAsync($"/api/material-receipts/{receiptDraft!.Id}/confirm", null)).EnsureSuccessStatusCode();

        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}"))!.CurrentQuantity.Should().Be(50.0000m);
        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{sugar.Id}"))!.CurrentQuantity.Should().Be(20.0000m);

        // Steps 3-5: Baker calculates recipe requirements for 200 pcs and finalizes production
        var operatingDay = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var preview = await baker.GetFromJsonAsync<ProductionRequirementPreviewDto>(
            $"/api/recipes/product/{pandesal.Id}/calculate?targetQuantity=200");
        preview!.AllIngredientsSufficient.Should().BeTrue();
        preview.Ingredients.Single(i => i.RawMaterialId == flour.Id).RequiredInBaseUnit.Should().Be(5.0000m);
        preview.Ingredients.Single(i => i.RawMaterialId == sugar.Id).RequiredInBaseUnit.Should().Be(0.5000m);

        var batch = await (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, pandesal.Id, 200m, 200m, operatingDay, null, "Morning Pandesal bake",
            [
                new ProductionConsumptionInput(flour.Id, 5.0m, kgUnit.Id),
                new ProductionConsumptionInput(sugar.Id, 500m, gUnit.Id)
            ]))).Content.ReadFromJsonAsync<ProductionBatchDto>();
        batch!.ActualQuantity.Should().Be(200.0000m);

        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}"))!.CurrentQuantity.Should().Be(45.0000m);
        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{sugar.Id}"))!.CurrentQuantity.Should().Be(19.5000m);
        (await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>("/api/factory-inventory/summary?search=E2E-PND-01"))!
            .Single().TotalAvailableQuantity.Should().Be(200.0000m);

        // Steps 6-8: Factory staff prepare & dispatch 150 pcs; Cashier confirms 148 pcs received (2 lost in transit)
        var deliveryDraft = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver Jose", "Morning store delivery", [new DeliveryItemInput(pandesal.Id, 150m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        (await baker.PostAsync($"/api/deliveries/{deliveryDraft!.Id}/prepare", null)).EnsureSuccessStatusCode();
        var dispatched = await (await baker.PostAsync($"/api/deliveries/{deliveryDraft.Id}/dispatch", null)).Content.ReadFromJsonAsync<DeliveryDto>();

        (await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>("/api/factory-inventory/summary?search=E2E-PND-01"))!
            .Single().TotalAvailableQuantity.Should().Be(50.0000m);

        var received = await (await cashier.PostAsJsonAsync($"/api/deliveries/{deliveryDraft.Id}/receive",
            new ConfirmDeliveryReceivingRequest("2 crushed on top tray",
                [new ReceiveDeliveryItemInput(dispatched!.Items.Single().Id, 148m, "2 pcs crushed in transit")]))).Content.ReadFromJsonAsync<DeliveryDto>();
        received!.Status.Should().Be(DeliveryStatus.PartiallyReceived);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{pandesal.Id}"))!.CurrentQuantity.Should().Be(148.0000m);

        // Manager reconciles the 2 pcs transit loss
        (await manager.PostAsJsonAsync($"/api/deliveries/{deliveryDraft.Id}/reconcile",
            new ReconcileDeliveryRequest(
                [new ReconcileDeliveryItemInput(received.Items.Single().Id, DiscrepancyResolutionType.TransitLossWriteOff, "Confirmed transit loss")])))
            .EnsureSuccessStatusCode();

        // Steps 9-10: Customers purchase 30 pcs + 25 pcs (55 pcs total) through POS -> Store drops to 93 pcs
        (await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"E2E-SALE1-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 100m, [new PosSaleItemInput(pandesal.Id, 30m)]))).EnsureSuccessStatusCode();
        (await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"E2E-SALE2-{Guid.NewGuid():N}", PaymentMethod.GCash, 0m, 75m, [new PosSaleItemInput(pandesal.Id, 25m)]))).EnsureSuccessStatusCode();
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{pandesal.Id}"))!.CurrentQuantity.Should().Be(93.0000m);

        // Step 11: 3 pcs become damaged on shelf and are recorded -> Store drops to 90 pcs
        (await cashier.PostAsJsonAsync("/api/waste-returns", new RecordWasteOrReturnRequest(
            InventoryLocation.Store, pandesal.Id, null, null, null, WasteReturnCategory.Damaged, 3m, "Squashed by display tongs")))
            .EnsureSuccessStatusCode();
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{pandesal.Id}"))!.CurrentQuantity.Should().Be(90.0000m);

        // Steps 12-15: ~7:00 PM physical stock count (counts 89, variance -1) -> Manager approves & carries 89 to next day
        var closingPreview = await cashier.GetFromJsonAsync<DailyClosingDto>($"/api/daily-closing/preview?date={operatingDay:yyyy-MM-dd}");
        var pndRow = closingPreview!.Items.Single(i => i.ProductId == pandesal.Id);
        pndRow.ExpectedClosingStock.Should().Be(90.0000m);

        var countInputs = closingPreview.Items.Select(i =>
            i.ProductId == pandesal.Id
                ? new SubmitDailyClosingItemInput(i.ProductId, 89m, "1 pc unaccounted during rush hour")
                : new SubmitDailyClosingItemInput(i.ProductId, i.ExpectedClosingStock)).ToList();

        var submittedClosing = await (await cashier.PostAsJsonAsync("/api/daily-closing/submit",
            new SubmitDailyClosingRequest(operatingDay, "7 PM daily closing", countInputs))).Content.ReadFromJsonAsync<DailyClosingDto>();
        submittedClosing!.Status.Should().Be(DailyClosingStatus.VarianceReviewPending);

        var finalizedClosing = await (await manager.PostAsJsonAsync($"/api/daily-closing/{submittedClosing.Id}/finalize",
            new FinalizeDailyClosingRequest(true, "Approved -1 variance"))).Content.ReadFromJsonAsync<DailyClosingDto>();
        finalizedClosing!.Status.Should().Be(DailyClosingStatus.Finalized);
        finalizedClosing.Items.Single(i => i.ProductId == pandesal.Id).CarryoverToNextDay.Should().Be(89.0000m);

        // Verify next operating day opens with 89 pcs without duplicating quantities
        var nextDay = operatingDay.AddDays(1);
        var nextDayPreview = await cashier.GetFromJsonAsync<DailyClosingDto>($"/api/daily-closing/preview?date={nextDay:yyyy-MM-dd}");
        var nextRow = nextDayPreview!.Items.Single(i => i.ProductId == pandesal.Id);
        nextRow.OpeningStock.Should().Be(89.0000m);
        nextRow.ExpectedClosingStock.Should().Be(89.0000m);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{pandesal.Id}"))!.CurrentQuantity.Should().Be(89.0000m);

        // Step 16: Owner reviews reports & exports + verifies ledger integrity & audit logs
        var salesReport = await owner.GetFromJsonAsync<SalesReportDto>("/api/reports/sales");
        salesReport!.BestSellingProducts.Should().Contain(p => p.ProductId == pandesal.Id && p.TotalQuantitySold == 55.0000m && p.GrossSalesRevenue == 165.00m);

        var prodReport = await owner.GetFromJsonAsync<ProductionReportDto>($"/api/reports/production?fromDate={operatingDay:yyyy-MM-dd}&toDate={operatingDay:yyyy-MM-dd}");
        prodReport!.ProductSummaries.Should().Contain(p => p.ProductId == pandesal.Id && p.TotalActualQuantity == 200.0000m);

        var closingReport = await owner.GetFromJsonAsync<ClosingReportDto>($"/api/reports/closing?fromDate={operatingDay:yyyy-MM-dd}&toDate={operatingDay:yyyy-MM-dd}");
        closingReport!.Closings.Should().ContainSingle(c => c.Id == finalizedClosing.Id);

        var verify = await owner.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
        verify.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentPosCheckout_AllowsOnlyOneWinnerAndNeverCreatesNegativeStock()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var cashier1 = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var cashier2 = await factory.CreateAuthenticatedClientAsync("test_cashier");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var rareCake = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "CONC-CAKE-01", "E2E Rare Cake", "Limited", pcsUnit.Id, 200m, 1m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        (await manager.PostAsJsonAsync("/api/store-inventory/adjustments/direct",
            new DirectStoreAdjustmentRequest(rareCake!.Id, 5m, "Exact 5 pcs opening stock", IsOpeningStock: true)))
            .EnsureSuccessStatusCode();

        // Fire two simultaneous requests each attempting to buy 4 of the 5 available pieces
        var t1 = cashier1.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"CONC-1-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 800m, [new PosSaleItemInput(rareCake.Id, 4m)]));
        var t2 = cashier2.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"CONC-2-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 800m, [new PosSaleItemInput(rareCake.Id, 4m)]));

        var responses = await Task.WhenAll(t1, t2);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict).Should().Be(1);

        var remaining = (await cashier1.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{rareCake.Id}"))!.CurrentQuantity;
        remaining.Should().Be(1.0000m);

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
    }
}
