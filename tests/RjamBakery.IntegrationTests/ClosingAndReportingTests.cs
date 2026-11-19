using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class ClosingAndReportingTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task WasteAndCustomerReturnInspection_SegregatesUnsellableStockCleanly()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var product = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P6-COC-01", "P6 Pan de Coco", "Sweet bun", pcsUnit.Id, 12m, 5m, 2, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        (await manager.PostAsJsonAsync("/api/store-inventory/adjustments/direct",
            new DirectStoreAdjustmentRequest(product!.Id, 40m, "Opening stock", IsOpeningStock: true)))
            .EnsureSuccessStatusCode();

        // Damaged 2 pcs -> immediately deducted from sellable stock (40 -> 38)
        var dmgResp = await cashier.PostAsJsonAsync("/api/waste-returns", new RecordWasteOrReturnRequest(
            InventoryLocation.Store, product.Id, null, null, null, WasteReturnCategory.Damaged, 2m, "Dropped on floor"));
        dmgResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{product.Id}"))!.CurrentQuantity.Should().Be(38.0000m);

        // Customer return 3 pcs -> PendingInspection, does NOT enter sellable stock yet
        var retResp = await cashier.PostAsJsonAsync("/api/waste-returns", new RecordWasteOrReturnRequest(
            InventoryLocation.Store, product.Id, null, null, null, WasteReturnCategory.CustomerReturn, 3m, "Wrong flavor bought"));
        retResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingRet = await retResp.Content.ReadFromJsonAsync<WasteAndReturnDto>();
        pendingRet!.InspectionOutcome.Should().Be(InspectionOutcome.PendingInspection);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{product.Id}"))!.CurrentQuantity.Should().Be(38.0000m);

        // Cashier cannot inspect/approve own return
        (await cashier.PostAsJsonAsync($"/api/waste-returns/{pendingRet.Id}/inspect",
            new InspectCustomerReturnRequest(InspectionOutcome.RestockedToSellable, "Cashier try")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager inspects and approves restock -> Store stock increases 38 -> 41
        var inspectResp = await manager.PostAsJsonAsync($"/api/waste-returns/{pendingRet.Id}/inspect",
            new InspectCustomerReturnRequest(InspectionOutcome.RestockedToSellable, "Sealed bag intact"));
        inspectResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{product.Id}"))!.CurrentQuantity.Should().Be(41.0000m);

        // Re-inspecting returns 409 Conflict
        (await manager.PostAsJsonAsync($"/api/waste-returns/{pendingRet.Id}/inspect",
            new InspectCustomerReturnRequest(InspectionOutcome.DisposedAsLoss, "Retry")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MultiDayClosingWorkflow_ComputesFormulaRequiresApprovalForVarianceAndCarriesOverWithoutDuplicating()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var bibingka = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P6-BIB-01", "P6 Bibingka", "Rice cake", pcsUnit.Id, 35m, 10m, 2, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        var day1 = DateOnly.FromDateTime(DateTime.UtcNow);

        // Day 1: Produce 80, deliver 50 to store, sell 12, spoil 3, restock return 2 => Expected = 0 + 50 + 2 - 12 - 3 = 37
        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, bibingka!.Id, 80m, 80m, day1, null, "Day 1 bake", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();

        var del1 = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver 1", "Day 1 dispatch", [new DeliveryItemInput(bibingka.Id, 50m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        var disp1 = await (await baker.PostAsync($"/api/deliveries/{del1!.Id}/dispatch", null)).Content.ReadFromJsonAsync<DeliveryDto>();
        (await cashier.PostAsJsonAsync($"/api/deliveries/{del1.Id}/receive",
            new ConfirmDeliveryReceivingRequest("OK", [new ReceiveDeliveryItemInput(disp1!.Items.Single().Id, 50m)])))
            .EnsureSuccessStatusCode();

        (await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"D1-SALE-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 500m, [new PosSaleItemInput(bibingka.Id, 12m)])))
            .EnsureSuccessStatusCode();

        (await cashier.PostAsJsonAsync("/api/waste-returns", new RecordWasteOrReturnRequest(
            InventoryLocation.Store, bibingka.Id, null, null, null, WasteReturnCategory.Spoiled, 3m, "Heat spoilage")))
            .EnsureSuccessStatusCode();

        var ret1 = await (await cashier.PostAsJsonAsync("/api/waste-returns", new RecordWasteOrReturnRequest(
            InventoryLocation.Store, bibingka.Id, null, null, null, WasteReturnCategory.CustomerReturn, 2m, "Unopened box return")))
            .Content.ReadFromJsonAsync<WasteAndReturnDto>();
        (await manager.PostAsJsonAsync($"/api/waste-returns/{ret1!.Id}/inspect",
            new InspectCustomerReturnRequest(InspectionOutcome.RestockedToSellable, "Intact")))
            .EnsureSuccessStatusCode();

        var previewDay1 = await cashier.GetFromJsonAsync<DailyClosingDto>($"/api/daily-closing/preview?date={day1:yyyy-MM-dd}");
        var bibRow1 = previewDay1!.Items.Single(i => i.ProductId == bibingka.Id);
        bibRow1.OpeningStock.Should().Be(0m);
        bibRow1.ConfirmedDeliveries.Should().Be(50.0000m);
        bibRow1.ApprovedRestockedReturns.Should().Be(2.0000m);
        bibRow1.TotalSold.Should().Be(12.0000m);
        bibRow1.RecordedWaste.Should().Be(3.0000m);
        bibRow1.ExpectedClosingStock.Should().Be(37.0000m);

        // Submitting physical count with variance (35 instead of 37) WITHOUT reason must fail
        var allInputsMissingReason = previewDay1.Items.Select(i =>
            i.ProductId == bibingka.Id
                ? new SubmitDailyClosingItemInput(i.ProductId, 35m, "")
                : new SubmitDailyClosingItemInput(i.ProductId, i.ExpectedClosingStock)).ToList();
        (await cashier.PostAsJsonAsync("/api/daily-closing/submit",
            new SubmitDailyClosingRequest(day1, "Day 1 closing", allInputsMissingReason)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Submit with valid variance reason -> VarianceReviewPending, StoreInventory STILL 37 (no silent overwrite!)
        var allInputsWithReason = previewDay1.Items.Select(i =>
            i.ProductId == bibingka.Id
                ? new SubmitDailyClosingItemInput(i.ProductId, 35m, "2 pcs missing on display")
                : new SubmitDailyClosingItemInput(i.ProductId, i.ExpectedClosingStock)).ToList();
        var submitResp = await cashier.PostAsJsonAsync("/api/daily-closing/submit",
            new SubmitDailyClosingRequest(day1, "Day 1 count", allInputsWithReason));
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var submittedDay1 = await submitResp.Content.ReadFromJsonAsync<DailyClosingDto>();
        submittedDay1!.Status.Should().Be(DailyClosingStatus.VarianceReviewPending);
        submittedDay1.Items.Single(i => i.ProductId == bibingka.Id).VarianceQuantity.Should().Be(-2.0000m);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{bibingka.Id}"))!.CurrentQuantity.Should().Be(37.0000m);

        // Cashier cannot finalize closing
        (await cashier.PostAsJsonAsync($"/api/daily-closing/{submittedDay1.Id}/finalize",
            new FinalizeDailyClosingRequest(true, "Cashier try"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager approves variance adjustment & finalizes Day 1 -> Store becomes 35, Carryover = 35
        var finalizeResp = await manager.PostAsJsonAsync($"/api/daily-closing/{submittedDay1.Id}/finalize",
            new FinalizeDailyClosingRequest(true, "Approved count adjustment"));
        finalizeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var finalizedDay1 = await finalizeResp.Content.ReadFromJsonAsync<DailyClosingDto>();
        finalizedDay1!.Status.Should().Be(DailyClosingStatus.Finalized);
        finalizedDay1.Items.Single(i => i.ProductId == bibingka.Id).CarryoverToNextDay.Should().Be(35.0000m);

        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{bibingka.Id}"))!.CurrentQuantity.Should().Be(35.0000m);

        // Day 2: Receive 20 more (35 + 20 = 55), sell 10 (55 - 10 = 45)
        var del2 = await (await baker.PostAsJsonAsync("/api/deliveries", new CreateDeliveryRequest(
            "Driver 1", "Day 2 dispatch", [new DeliveryItemInput(bibingka.Id, 20m)]))).Content.ReadFromJsonAsync<DeliveryDto>();
        var disp2 = await (await baker.PostAsync($"/api/deliveries/{del2!.Id}/dispatch", null)).Content.ReadFromJsonAsync<DeliveryDto>();
        (await cashier.PostAsJsonAsync($"/api/deliveries/{del2.Id}/receive",
            new ConfirmDeliveryReceivingRequest("OK", [new ReceiveDeliveryItemInput(disp2!.Items.Single().Id, 20m)])))
            .EnsureSuccessStatusCode();

        (await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"D2-SALE-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 400m, [new PosSaleItemInput(bibingka.Id, 10m)])))
            .EnsureSuccessStatusCode();

        var day2 = day1.AddDays(1);
        var previewDay2 = await cashier.GetFromJsonAsync<DailyClosingDto>($"/api/daily-closing/preview?date={day2:yyyy-MM-dd}");
        var bibRow2 = previewDay2!.Items.Single(i => i.ProductId == bibingka.Id);
        bibRow2.OpeningStock.Should().Be(35.0000m);
        bibRow2.ConfirmedDeliveries.Should().Be(20.0000m);
        bibRow2.TotalSold.Should().Be(10.0000m);
        bibRow2.ExpectedClosingStock.Should().Be(45.0000m);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{bibingka.Id}"))!.CurrentQuantity.Should().Be(45.0000m);

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
        verify.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationalReportsAndPdfExcelExports_ReturnAccurateDataAndValidBinaries()
    {
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");

        // RBAC check on reports
        (await cashier.GetAsync("/api/reports/sales")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.GetAsync("/api/reports/inventory")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var invReport = await manager.GetFromJsonAsync<InventoryReportDto>("/api/reports/inventory");
        invReport.Should().NotBeNull();

        var prodReport = await manager.GetFromJsonAsync<ProductionReportDto>("/api/reports/production");
        prodReport.Should().NotBeNull();

        var salesReport = await manager.GetFromJsonAsync<SalesReportDto>("/api/reports/sales");
        salesReport.Should().NotBeNull();

        var delReport = await manager.GetFromJsonAsync<DeliveryReportDto>("/api/reports/deliveries");
        delReport.Should().NotBeNull();

        var closingReport = await manager.GetFromJsonAsync<ClosingReportDto>("/api/reports/closing");
        closingReport.Should().NotBeNull();

        foreach (var endpoint in new[] { "inventory", "production", "sales", "deliveries", "closing" })
        {
            var xlsxResp = await manager.GetAsync($"/api/reports/{endpoint}/export?format=xlsx");
            xlsxResp.StatusCode.Should().Be(HttpStatusCode.OK);
            xlsxResp.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            var xlsxBytes = await xlsxResp.Content.ReadAsByteArrayAsync();
            xlsxBytes.Length.Should().BeGreaterThan(500);
            Encoding.ASCII.GetString(xlsxBytes, 0, 2).Should().Be("PK");

            var pdfResp = await manager.GetAsync($"/api/reports/{endpoint}/export?format=pdf");
            pdfResp.StatusCode.Should().Be(HttpStatusCode.OK);
            pdfResp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            var pdfBytes = await pdfResp.Content.ReadAsByteArrayAsync();
            pdfBytes.Length.Should().BeGreaterThan(500);
            Encoding.ASCII.GetString(pdfBytes, 0, 5).Should().Be("%PDF-");
        }
    }
}
