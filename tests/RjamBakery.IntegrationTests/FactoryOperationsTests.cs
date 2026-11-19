using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Enums;
using RjamBakery.Web.Endpoints;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class FactoryOperationsTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task SupplierAndMaterialReceiving_OnlyIncreasesInventoryOnConfirmationAndBlocksDuplicates()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var kgUnit = units!.Single(u => u.Code == "kg");
        var gUnit = units!.Single(u => u.Code == "g");
        var packUnit = units!.Single(u => u.Code == "pack");

        var flour = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "RM-P3-FLR", "P3 Flour", "", kgUnit.Id, 25m, 10m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();
        var sugar = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "RM-P3-SGR", "P3 Sugar", "", kgUnit.Id, null, 5m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();

        var supResp = await owner.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(
            null, "Manila Milling Co.", "Ana Cruz", "09171234567", "ana@milling.test", "Pasig City", SupplierStatus.Active, "Primary flour supplier",
            [
                new SupplierMaterialLinkRequest(flour!.Id, 1100m, 2),
                new SupplierMaterialLinkRequest(sugar!.Id, 65m, 2)
            ]));
        supResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var supplier = await supResp.Content.ReadFromJsonAsync<SupplierDto>();
        supplier!.SuppliedMaterials.Should().HaveCount(2);

        // Baker creates draft receipt: 2 packs (2 * 25 kg = 50 kg) Flour + 10000 g (10 kg) Sugar
        var draftResp = await baker.PostAsJsonAsync("/api/material-receipts", new CreateMaterialReceiptRequest(
            supplier.Id,
            DateTime.UtcNow,
            "Morning delivery",
            [
                new MaterialReceiptItemInput(flour.Id, 2m, packUnit.Id, 1100m),
                new MaterialReceiptItemInput(sugar.Id, 10000m, gUnit.Id, 0.065m)
            ]));
        draftResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var draft = await draftResp.Content.ReadFromJsonAsync<MaterialReceiptDto>();
        draft!.Status.Should().Be(MaterialReceiptStatus.Draft);
        draft.TotalCost.Should().Be(2850.00m);

        // Inventory MUST remain 0 while still in Draft
        var flourBefore = await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}");
        flourBefore!.CurrentQuantity.Should().Be(0m);

        // Confirm receipt -> increases raw material stock via ledger
        var confirmResp = await baker.PostAsync($"/api/material-receipts/{draft.Id}/confirm", null);
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = await confirmResp.Content.ReadFromJsonAsync<MaterialReceiptDto>();
        confirmed!.Status.Should().Be(MaterialReceiptStatus.Confirmed);

        var flourAfter = await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}");
        var sugarAfter = await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{sugar.Id}");
        flourAfter!.CurrentQuantity.Should().Be(50.0000m);
        sugarAfter!.CurrentQuantity.Should().Be(10.0000m);

        // Re-confirming same receipt must return 409 Conflict
        var reconfirm = await baker.PostAsync($"/api/material-receipts/{draft.Id}/confirm", null);
        reconfirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ProductionWorkflow_ScalesRecipeBlocksShortagesCreatesLotAndPreservesRecipeVersion()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var kgUnit = units!.Single(u => u.Code == "kg");
        var gUnit = units!.Single(u => u.Code == "g");
        var pcsUnit = units!.Single(u => u.Code == "pcs");

        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        var breadCat = categories!.First();

        var prod = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, breadCat.Id, "P3-PND-01", "P3 Pandesal", "Test pandesal", pcsUnit.Id, 4.00m, 50m, 2, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        var flour = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "RM-PRD-FLR", "Production Flour", "", kgUnit.Id, null, 5m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();
        var yeast = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "RM-PRD-YST", "Production Yeast", "", gUnit.Id, null, 50m, null))).Content.ReadFromJsonAsync<RawMaterialDto>();

        var supplier = await (await owner.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(
            null, "Bakers Supply", "Leo", "0918", "", "", SupplierStatus.Active, ""))).Content.ReadFromJsonAsync<SupplierDto>();

        // Receive 20 kg Flour ONLY (no Yeast yet)
        var rcv1 = await (await baker.PostAsJsonAsync("/api/material-receipts", new CreateMaterialReceiptRequest(
            supplier!.Id, DateTime.UtcNow, "Flour only",
            [new MaterialReceiptItemInput(flour!.Id, 20m, kgUnit.Id, 100m)]))).Content.ReadFromJsonAsync<MaterialReceiptDto>();
        (await baker.PostAsync($"/api/material-receipts/{rcv1!.Id}/confirm", null)).EnsureSuccessStatusCode();

        // Owner creates Recipe v1: 100 pcs yields from 2.5 kg Flour + 30 g Yeast
        var rec1Resp = await owner.PostAsJsonAsync("/api/recipes", new SaveRecipeRequest(
            prod!.Id, 100m, pcsUnit.Id, "Initial test recipe",
            [
                new RecipeIngredientInput(flour.Id, 2.5m, kgUnit.Id),
                new RecipeIngredientInput(yeast!.Id, 30m, gUnit.Id)
            ]));
        rec1Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipeV1 = await rec1Resp.Content.ReadFromJsonAsync<RecipeDto>();
        recipeV1!.VersionNumber.Should().Be(1);

        // Preview 200 pcs -> needs 5 kg Flour (ok) & 60 g Yeast (short by 60 g)
        var previewShort = await baker.GetFromJsonAsync<ProductionRequirementPreviewDto>(
            $"/api/recipes/product/{prod.Id}/calculate?targetQuantity=200");
        previewShort!.AllIngredientsSufficient.Should().BeFalse();
        previewShort.Ingredients.Single(i => i.RawMaterialId == yeast.Id).ShortfallInBaseUnit.Should().Be(60.0000m);

        // Attempt production finalization while Yeast is short -> MUST FAIL & NOT deduct Flour!
        var prodDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var failProd = await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, prod.Id, 200m, 200m, prodDate, null, "Should fail",
            [
                new ProductionConsumptionInput(flour.Id, 5m, kgUnit.Id),
                new ProductionConsumptionInput(yeast.Id, 60m, gUnit.Id)
            ]));
        failProd.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var flourCheckAfterFail = await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}");
        flourCheckAfterFail!.CurrentQuantity.Should().Be(20.0000m);

        // Receive 500 g Yeast & confirm
        var rcv2 = await (await baker.PostAsJsonAsync("/api/material-receipts", new CreateMaterialReceiptRequest(
            supplier.Id, DateTime.UtcNow, "Yeast arrival",
            [new MaterialReceiptItemInput(yeast.Id, 500m, gUnit.Id, 2m)]))).Content.ReadFromJsonAsync<MaterialReceiptDto>();
        (await baker.PostAsync($"/api/material-receipts/{rcv2!.Id}/confirm", null)).EnsureSuccessStatusCode();

        // Finalize 200 pcs production
        var okProdResp = await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, prod.Id, 200m, 200m, prodDate, null, "Morning batch",
            [
                new ProductionConsumptionInput(flour.Id, 5m, kgUnit.Id),
                new ProductionConsumptionInput(yeast.Id, 60m, gUnit.Id)
            ],
            IdempotencyKey: $"PRD-TEST-{Guid.NewGuid():N}"[..32]));
        okProdResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var batch1 = await okProdResp.Content.ReadFromJsonAsync<ProductionBatchDto>();
        batch1!.RecipeVersionSnapshot.Should().Be(1);
        batch1.ActualQuantity.Should().Be(200m);

        // Verify ingredient deductions
        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}"))!.CurrentQuantity.Should().Be(15.0000m);
        (await baker.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{yeast.Id}"))!.CurrentQuantity.Should().Be(440.0000m);

        // Verify Factory Stock Lot created with ShelfLifeDays (2 days)
        var lots = await baker.GetFromJsonAsync<PagedResult<FactoryStockLotDto>>($"/api/factory-inventory/lots?productId={prod.Id}");
        lots!.Items.Should().ContainSingle();
        var lot1 = lots.Items[0];
        lot1.RemainingQuantity.Should().Be(200.0000m);
        lot1.ExpirationDate.Should().Be(prodDate.AddDays(2));

        // Update Recipe to v2 (changing Flour requirement to 3.0 kg per 100 pcs)
        var rec2Resp = await manager.PostAsJsonAsync("/api/recipes", new SaveRecipeRequest(
            prod.Id, 100m, pcsUnit.Id, "Updated flour ratio",
            [
                new RecipeIngredientInput(flour.Id, 3.0m, kgUnit.Id),
                new RecipeIngredientInput(yeast.Id, 35m, gUnit.Id)
            ]));
        rec2Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipeV2 = await rec2Resp.Content.ReadFromJsonAsync<RecipeDto>();
        recipeV2!.VersionNumber.Should().Be(2);

        // Historical batch 1 MUST still retain v1 snapshot & 5.0 kg Flour snapshot
        var historicalBatch1 = await baker.GetFromJsonAsync<ProductionBatchDto>($"/api/production/{batch1.Id}");
        historicalBatch1!.RecipeVersionSnapshot.Should().Be(1);
        historicalBatch1.Consumptions.Single(c => c.RawMaterialId == flour.Id).SnapshotRecipeQuantity.Should().Be(5.0000m);

        // Ledger integrity stays 100% balanced
        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
        verify.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task FactoryInventory_FefoAllocationLotExpiryAndPhase3Rbac_WorkCorrectly()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var cake = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P3-CAKE-01", "P3 Cake Roll", "Test", pcsUnit.Id, 120m, 5m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        // Create Lot 1 expiring later, then Lot 2 expiring earlier (earlier expiry should allocate FIRST under FEFO)
        var baseDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var laterExpiry = baseDate.AddDays(9);
        var earlierExpiry = baseDate.AddDays(4);

        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, cake!.Id, 10m, 10m, baseDate.AddDays(-1), laterExpiry, "Later expiry batch", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();

        (await baker.PostAsJsonAsync("/api/production/finalize", new FinalizeProductionRequest(
            null, cake.Id, 8m, 8m, baseDate, earlierExpiry, "Earlier expiry batch", [], ConfirmNoRecipe: true)))
            .EnsureSuccessStatusCode();

        var fefo = await baker.GetFromJsonAsync<List<FefoAllocationPreviewDto>>(
            $"/api/factory-inventory/fefo-preview?productId={cake.Id}&quantity=12");
        fefo.Should().HaveCount(2);
        fefo![0].ExpirationDate.Should().Be(earlierExpiry);
        fefo[0].AllocatedQuantity.Should().Be(8m);
        fefo[1].ExpirationDate.Should().Be(laterExpiry);
        fefo[1].AllocatedQuantity.Should().Be(4m);

        // Manager marks the earlier-expiry lot as Expired -> remaining drops to 0 and writes off in ledger
        var expireResp = await manager.PostAsJsonAsync($"/api/factory-inventory/lots/{fefo[0].FactoryStockLotId}/status",
            new LotStatusUpdateRequest(FactoryLotStatus.Expired, "Past freshness window"));
        expireResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var summary = await baker.GetFromJsonAsync<List<FactoryProductSummaryDto>>($"/api/factory-inventory/summary?search=P3-CAKE-01");
        summary!.Single().TotalAvailableQuantity.Should().Be(10.0000m);

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();

        // Phase 3 RBAC enforcement checks
        (await cashier.GetAsync("/api/suppliers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/material-receipts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/production")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/factory-inventory/lots")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await baker.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(
            null, "Baker Try", "", "", "", "", SupplierStatus.Active, ""))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.PostAsJsonAsync($"/api/factory-inventory/lots/{fefo[1].FactoryStockLotId}/status",
            new LotStatusUpdateRequest(FactoryLotStatus.Quarantined, "Baker try"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
