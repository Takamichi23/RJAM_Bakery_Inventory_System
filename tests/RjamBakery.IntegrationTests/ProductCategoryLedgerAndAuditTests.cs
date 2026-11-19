using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Infrastructure.Persistence;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class ProductCategoryLedgerAndAuditTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task ProductAndCategoryWorkflow_ArchivePreservesRecordAndWritesAuditLog()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        categories.Should().NotBeNullOrEmpty();

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");

        var catResp = await owner.PostAsJsonAsync("/api/categories", new SaveCategoryRequest(
            null, "Test Artisan Loaves", "Test category", 20, true));
        catResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdCategory = await catResp.Content.ReadFromJsonAsync<CategoryDto>();

        var prodResp = await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, createdCategory!.Id, "tst-pnd-01", "Test Pandesal", "Soft roll", pcsUnit.Id, 5.00m, 20m, 2, null));
        prodResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdProduct = await prodResp.Content.ReadFromJsonAsync<ProductDto>();
        createdProduct!.Sku.Should().Be("TST-PND-01");

        // Duplicate SKU must fail
        var dupResp = await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, createdCategory.Id, "TST-PND-01", "Duplicate", "", pcsUnit.Id, 5.00m, 10m, null, null));
        dupResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Archive hides from default query, keeps GetById & includeArchived
        var archiveResp = await owner.PatchAsJsonAsync($"/api/products/{createdProduct.Id}/archive", new { Archive = true });
        archiveResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activeList = await owner.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?search=TST-PND-01");
        activeList!.Items.Should().BeEmpty();

        var archivedList = await owner.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?search=TST-PND-01&includeArchived=true");
        archivedList!.Items.Should().ContainSingle(p => p.Id == createdProduct.Id && p.IsArchived);

        var byId = await owner.GetFromJsonAsync<ProductDto>($"/api/products/{createdProduct.Id}");
        byId!.IsArchived.Should().BeTrue();

        // Audit logs captured create + archive
        var audits = await owner.GetFromJsonAsync<PagedResult<AuditLogDto>>("/api/audit-logs?entityName=Product");
        audits!.Items.Should().Contain(a => a.EntityId == createdProduct.Id.ToString() && a.Action == "CreateProduct");
        audits.Items.Should().Contain(a => a.EntityId == createdProduct.Id.ToString() && a.Action == "ArchiveProduct");
    }

    [Fact]
    public async Task RawMaterialAndLedger_UnitConversionNegativeBlockDuplicatePreventionAndReconciliation()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var kgUnit = units!.Single(u => u.Code == "kg");
        var gUnit = units!.Single(u => u.Code == "g");
        var lUnit = units!.Single(u => u.Code == "L");

        var rmResp = await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "RM-FLOUR-TEST", "Test Bread Flour", "Test ingredient", kgUnit.Id, 25m, 10m, null));
        rmResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var flour = await rmResp.Content.ReadFromJsonAsync<RawMaterialDto>();

        // Post Opening Stock +50 kg
        var openKey = $"OPEN-{Guid.NewGuid():N}";
        var openAdj = await manager.PostAsJsonAsync($"/api/raw-materials/{flour!.Id}/adjust",
            new RawMaterialAdjustmentRequest(flour.Id, 50m, kgUnit.Id, "Initial test flour stock", openKey, IsOpeningStock: true));
        openAdj.StatusCode.Should().Be(HttpStatusCode.OK);
        var openMovement = await openAdj.Content.ReadFromJsonAsync<InventoryMovementDto>();
        openMovement!.PreviousQuantity.Should().Be(0m);
        openMovement.NewQuantity.Should().Be(50.0000m);

        // Duplicate idempotency key must return 409 Conflict
        var dupAdj = await manager.PostAsJsonAsync($"/api/raw-materials/{flour.Id}/adjust",
            new RawMaterialAdjustmentRequest(flour.Id, 50m, kgUnit.Id, "Retry duplicate", openKey, IsOpeningStock: true));
        dupAdj.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Adjust -500 g -> converts to -0.5000 kg => 49.5000 kg
        var gramAdj = await manager.PostAsJsonAsync($"/api/raw-materials/{flour.Id}/adjust",
            new RawMaterialAdjustmentRequest(flour.Id, -500m, gUnit.Id, "Sample quality test usage"));
        gramAdj.StatusCode.Should().Be(HttpStatusCode.OK);
        var gramMovement = await gramAdj.Content.ReadFromJsonAsync<InventoryMovementDto>();
        gramMovement!.QuantityDelta.Should().Be(-0.5000m);
        gramMovement.PreviousQuantity.Should().Be(50.0000m);
        gramMovement.NewQuantity.Should().Be(49.5000m);

        // Incompatible unit (Liter on kg material) must be rejected
        var badUnitResp = await manager.PostAsJsonAsync($"/api/raw-materials/{flour.Id}/adjust",
            new RawMaterialAdjustmentRequest(flour.Id, -1m, lUnit.Id, "Wrong dimension"));
        badUnitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Negative stock over-deduction (-100 kg) must be blocked
        var overDeductResp = await manager.PostAsJsonAsync($"/api/raw-materials/{flour.Id}/adjust",
            new RawMaterialAdjustmentRequest(flour.Id, -100m, kgUnit.Id, "Over deduction test"));
        overDeductResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var currentFlour = await owner.GetFromJsonAsync<RawMaterialDto>($"/api/raw-materials/{flour.Id}");
        currentFlour!.CurrentQuantity.Should().Be(49.5000m);

        // Ledger balance reconciliation must pass with zero mismatches
        var reconciliation = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        reconciliation!.IsBalanced.Should().BeTrue();
        reconciliation.Mismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RoleScopedLedgerAndSystemSettings_WorkCorrectly()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");

        // Post a store movement via scoped service to test role-based ledger scoping
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var accessor = scope.ServiceProvider.GetRequiredService<ScopedUserAccessor>();
            var ownerUser = await db.Users.FirstAsync(u => u.UserName == "test_owner");
            accessor.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ownerUser.Id),
                new Claim(ClaimTypes.Name, ownerUser.UserName!),
                new Claim(ClaimTypes.Role, AppRoles.Owner)
            ], "TestAuth"));

            var units = await db.UnitsOfMeasure.ToListAsync();
            var pcs = units.First(u => u.Code == "pcs");
            var cat = await db.ProductCategories.FirstAsync();
            var prodService = scope.ServiceProvider.GetRequiredService<IProductService>();
            var prod = await prodService.SaveAsync(new SaveProductRequest(
                null, cat.Id, "LED-PROD-01", "Ledger Test Bun", "", pcs.Id, 10m, 5m, null, null));

            var ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedgerService>();
            await ledger.RecordStoreMovementAsync(new RecordStoreMovementRequest(
                prod.Id, 15m, InventoryLocation.InTransit, InventoryLocation.Store,
                InventoryMovementType.StoreReceiving, "TestDelivery", "999", "DEL-TEST-999",
                "Store receiving test", DeduplicationKey: $"TEST-STORE-{Guid.NewGuid():N}"));
        }

        // Baker sees no Store movements; Cashier sees Store movements & no RawMaterial movements
        var bakerMovements = await baker.GetFromJsonAsync<PagedResult<InventoryMovementDto>>("/api/ledger/movements?pageSize=100");
        bakerMovements!.Items.Should().NotContain(m => m.DestinationLocation == InventoryLocation.Store || m.SourceLocation == InventoryLocation.Store);

        var cashierMovements = await cashier.GetFromJsonAsync<PagedResult<InventoryMovementDto>>("/api/ledger/movements?pageSize=100");
        cashierMovements!.Items.Should().NotBeEmpty();
        cashierMovements.Items.Should().OnlyContain(m => m.SourceLocation == InventoryLocation.Store || m.DestinationLocation == InventoryLocation.Store);

        // Owner updates system setting
        var setResp = await owner.PutAsJsonAsync("/api/settings", new UpdateSystemSettingRequest("FactoryOpenTime", "05:30"));
        setResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var opConfig = await cashier.GetFromJsonAsync<OperationalConfigDto>("/api/settings/operational");
        opConfig!.FactoryOpenTime.Should().Be("05:30");

        // Reconciliation stays balanced after store movement
        var verify = await owner.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task AutoSkuGeneration_CreatesSequentialUniqueSkusAndPreservesOnBlankEdit()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");
        var catId = categories!.First().Id;
        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsId = units!.Single(u => u.Code == "pcs").Id;
        var kgId = units!.Single(u => u.Code == "kg").Id;

        var p1 = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, catId, null, $"Auto Prod A {Guid.NewGuid():N}", "", pcsId, 12m, 5m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();
        var p2 = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, catId, "   ", $"Auto Prod B {Guid.NewGuid():N}", "", pcsId, 15m, 5m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        p1!.Sku.Should().MatchRegex(@"^PRD-\d{4}$");
        p2!.Sku.Should().MatchRegex(@"^PRD-\d{4}$");
        p2.Sku.Should().NotBe(p1.Sku);

        // Editing with blank SKU preserves existing generated SKU
        var updatedP1 = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            p1.Id, catId, "", p1.Name, "Updated desc", pcsId, 14m, 5m, null, null)))
            .Content.ReadFromJsonAsync<ProductDto>();
        updatedP1!.Sku.Should().Be(p1.Sku);

        var rm1 = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, null, $"Auto Flour A {Guid.NewGuid():N}", "", kgId, null, 2m, null)))
            .Content.ReadFromJsonAsync<RawMaterialDto>();
        var rm2 = await (await owner.PostAsJsonAsync("/api/raw-materials", new SaveRawMaterialRequest(
            null, "", $"Auto Sugar B {Guid.NewGuid():N}", "", kgId, null, 2m, null)))
            .Content.ReadFromJsonAsync<RawMaterialDto>();

        rm1!.Sku.Should().MatchRegex(@"^RM-\d{4}$");
        rm2!.Sku.Should().MatchRegex(@"^RM-\d{4}$");
        rm2.Sku.Should().NotBe(rm1.Sku);
    }
}
