using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Web.Components.Pages.Pos;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class PosAndSalesTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task PosSaleFlow_ValidatesStockPreventsDuplicatesAndDeductsStoreInventory()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");

        var units = await owner.GetFromJsonAsync<List<UnitOfMeasureDto>>("/api/units");
        var pcsUnit = units!.Single(u => u.Code == "pcs");
        var categories = await owner.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        var croissant = await (await owner.PostAsJsonAsync("/api/products", new SaveProductRequest(
            null, categories!.First().Id, "P5-CRS-01", "P5 Butter Croissant", "Flaky pastry", pcsUnit.Id, 15.00m, 5m, 2, null)))
            .Content.ReadFromJsonAsync<ProductDto>();

        // Stock 20 pcs into Store via Manager direct adjustment (opening store stock)
        (await manager.PostAsJsonAsync("/api/store-inventory/adjustments/direct",
            new DirectStoreAdjustmentRequest(croissant!.Id, 20m, "Initial store display stock", IsOpeningStock: true)))
            .EnsureSuccessStatusCode();

        // Cashier fetches POS catalog
        var catalog = await cashier.GetFromJsonAsync<List<PosCatalogItemDto>>("/api/pos/catalog?search=P5-CRS-01");
        catalog!.Should().ContainSingle(c => c.ProductId == croissant.Id && c.AvailableStoreStock == 20.0000m);

        // Insufficient payment rejected
        var underpaid = await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"POS-UNDER-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 10m, [new PosSaleItemInput(croissant.Id, 2m)]));
        underpaid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Unavailable quantity rejected
        var oversell = await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"POS-OVER-{Guid.NewGuid():N}", PaymentMethod.Cash, 0m, 999m, [new PosSaleItemInput(croissant.Id, 50m)]));
        oversell.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Valid checkout: 3 * 15 = 45, discount 5 => total 40, tendered 100 => change 60
        var idemKey = $"POS-SALE-{Guid.NewGuid():N}";
        var saleResp = await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            idemKey, PaymentMethod.Cash, 5.00m, 100.00m, [new PosSaleItemInput(croissant.Id, 3m)]));
        saleResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var receipt = await saleResp.Content.ReadFromJsonAsync<PosReceiptDto>();
        receipt!.Transaction.SubTotal.Should().Be(45.00m);
        receipt.Transaction.DiscountAmount.Should().Be(5.00m);
        receipt.Transaction.TotalAmount.Should().Be(40.00m);
        receipt.Transaction.ChangeAmount.Should().Be(60.00m);
        receipt.Transaction.ReceiptNumber.Should().StartWith("RCP-");

        // Duplicate idempotency key returns 409 Conflict and does NOT deduct twice
        var dupSale = await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            idemKey, PaymentMethod.Cash, 5.00m, 100.00m, [new PosSaleItemInput(croissant.Id, 3m)]));
        dupSale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var storeAfter = await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{croissant.Id}");
        storeAfter!.CurrentQuantity.Should().Be(17.0000m);
        storeAfter.LastSoldAt.Should().NotBeNull();

        // Baker is denied POS operations
        (await baker.GetAsync("/api/pos/catalog")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Cashier is denied Void/Refund reversal
        (await cashier.PostAsJsonAsync($"/api/pos/transactions/{receipt.Transaction.Id}/reverse",
            new ReversePosTransactionRequest(PosTransactionStatus.Voided, "Cashier try"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager voids Sale -> restores 3 pcs back to Store (20 pcs)
        var voidResp = await manager.PostAsJsonAsync($"/api/pos/transactions/{receipt.Transaction.Id}/reverse",
            new ReversePosTransactionRequest(PosTransactionStatus.Voided, "Wrong item rung up"));
        voidResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await voidResp.Content.ReadFromJsonAsync<PosTransactionDto>())!.Status.Should().Be(PosTransactionStatus.Voided);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{croissant.Id}"))!.CurrentQuantity.Should().Be(20.0000m);

        // Re-voiding returns 409 Conflict
        (await manager.PostAsJsonAsync($"/api/pos/transactions/{receipt.Transaction.Id}/reverse",
            new ReversePosTransactionRequest(PosTransactionStatus.Voided, "Retry"))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Sale #2 followed by Refund WITHOUT immediate restock -> routes to PendingInspection, Store remains 16 pcs
        var sale2 = await (await cashier.PostAsJsonAsync("/api/pos/checkout", new CompletePosSaleRequest(
            $"POS-SALE2-{Guid.NewGuid():N}", PaymentMethod.GCash, 0m, 60.00m, [new PosSaleItemInput(croissant.Id, 4m)])))
            .Content.ReadFromJsonAsync<PosReceiptDto>();

        var refundResp = await manager.PostAsJsonAsync($"/api/pos/transactions/{sale2!.Transaction.Id}/reverse",
            new ReversePosTransactionRequest(PosTransactionStatus.Refunded, "Customer opened box", RestoreToSellableStock: false));
        refundResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cashier.GetFromJsonAsync<StoreInventoryDto>($"/api/store-inventory/{croissant.Id}"))!.CurrentQuantity.Should().Be(16.0000m);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pendingReturn = await db.WasteAndReturnRecords.AsNoTracking()
                .FirstOrDefaultAsync(w => w.PosTransactionId == sale2.Transaction.Id);
            pendingReturn.Should().NotBeNull();
            pendingReturn!.InspectionOutcome.Should().Be(InspectionOutcome.PendingInspection);
            pendingReturn.Quantity.Should().Be(4.0000m);
        }

        var verify = await manager.GetFromJsonAsync<LedgerReconciliationResult>("/api/ledger/verify");
        verify!.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void PosTerminalComponent_AddsItemToCartAndCalculatesTotal()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("cashier1");
        auth.SetRoles(AppRoles.Cashier);

        ctx.Services.AddSingleton<ISystemSettingService>(new StubSettingService());
        ctx.Services.AddSingleton<ICategoryService>(new StubCategoryService());
        ctx.Services.AddSingleton<IPosService>(new StubPosService());

        ctx.RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = ctx.RenderComponent<PosTerminal>();

        cut.Markup.Should().Contain("Chocolate Cookie");
        cut.FindAll("button").First(b => b.TextContent.Contains("Add to cart")).Click();
        cut.Markup.Should().Contain("Current Order");
        cut.Markup.Should().Contain("₱5.00");
    }

    private sealed class StubSettingService : ISystemSettingService
    {
        public Task<OperationalConfigDto> GetOperationalConfigAsync(CancellationToken ct = default)
            => Task.FromResult(new OperationalConfigDto("RJAM Bakery", "₱", "PHP", "06:00", "18:00", "06:00", "19:00", false));
        public Task<IReadOnlyList<SystemSettingDto>> GetAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SystemSettingDto> UpdateAsync(UpdateSystemSettingRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubCategoryService : ICategoryService
    {
        public Task<IReadOnlyList<CategoryDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CategoryDto>>([new CategoryDto(1, "Cookies", "", 1, true, 1)]);
        public Task<CategoryDto> SaveAsync(SaveCategoryRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetActiveStatusAsync(int categoryId, bool isActive, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPosService : IPosService
    {
        public Task<IReadOnlyList<PosCatalogItemDto>> GetPosCatalogAsync(string? searchTerm = null, int? categoryId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PosCatalogItemDto>>(
            [
                new PosCatalogItemDto(10, "CK-01", "Chocolate Cookie", "Soft baked cookie", 1, "Cookies", "pcs", 5.00m, null, 12m, 3m)
            ]);
        public Task<PosReceiptDto> CompleteSaleAsync(CompletePosSaleRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PosReceiptDto> GetReceiptAsync(long transactionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PagedResult<PosTransactionDto>> GetTransactionsPagedAsync(PosTransactionQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PosTransactionDto> ReverseTransactionAsync(long transactionId, ReversePosTransactionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
