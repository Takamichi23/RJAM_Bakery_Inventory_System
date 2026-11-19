using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Web.Components.Pages.Catalog;
using RjamBakery.Web.Components.Pages.Factory;
using RjamBakery.Web.Components.Pages.Pos;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class InteractiveUiFlowTests(TestWebApplicationFactory factory)
{
    private static readonly string[] AllPageRoutes =
    [
        "/",
        "/products",
        "/raw-materials",
        "/suppliers",
        "/production",
        "/factory-inventory",
        "/deliveries",
        "/store-inventory",
        "/pos",
        "/sales-history",
        "/returns-waste",
        "/daily-closing",
        "/reports",
        "/audit-logs",
        "/users",
        "/settings"
    ];

    [Fact]
    public async Task HttpRoutes_AuthenticatedOwnerLoadsAllPagesWithCleanLayoutAndAssets()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        foreach (var route in AllPageRoutes)
        {
            var response = await owner.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"route '{route}' should return 200 OK for Owner");

            var html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("MudBlazor.min.css");
            Regex.IsMatch(html, @"app(\.[a-z0-9]+)?\.css").Should().BeTrue();
            Regex.IsMatch(html, @"rjam-favicon(\.[a-z0-9]+)?\.png").Should().BeTrue();
            Regex.IsMatch(html, @"pos-offline(\.[a-z0-9]+)?\.js").Should().BeTrue();
            html.Should().Contain("_framework/blazor.web.js");
            html.Should().NotContain("fonts.googleapis.com");
            html.Should().NotContain("fonts.gstatic.com");
            html.Should().NotContain("An unhandled exception occurred");
            html.Should().NotContain("Unhandled exception");
        }

        foreach (var asset in new[]
        {
            "/images/rjam-logo.png",
            "/images/rjam-logo-light.png",
            "/images/rjam-mark.png",
            "/images/rjam-favicon.png",
            "/offline-pos.html",
            "/js/auth-ui.js",
            "/js/pos-offline.js",
            "/sw.js",
            "/manifest.json"
        })
        {
            var assetResp = await owner.GetAsync(asset);
            assetResp.StatusCode.Should().Be(HttpStatusCode.OK, $"static asset '{asset}' must be served");
        }
    }

    [Fact]
    public async Task HttpRoutes_CashierDeniedAdminPagesWithoutServerErrors()
    {
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");

        foreach (var restricted in new[] { "/users", "/settings", "/audit-logs", "/products" })
        {
            var response = await cashier.GetAsync(restricted);
            response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("User Management — Create Staff Account");
            body.Should().NotContain("Save Product");
        }
    }

    [Fact]
    public async Task AuthSplashAndNotFound404Routes_RenderBrandedScreensCleanly()
    {
        using var anon = factory.CreateAnonymousClient();
        var loginHtml = await (await anon.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        loginHtml.Should().Contain("rjam-prelogin-splash");
        loginHtml.Should().Contain("rjam-auth-submit-overlay");
        loginHtml.Should().Contain("Preparing RJAM Bakery...");

        var anonNotFound = await anon.GetAsync("/this-route-does-not-exist-404");
        anonNotFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var anonNotFoundHtml = await anonNotFound.Content.ReadAsStringAsync();
        anonNotFoundHtml.Should().Contain("404");
        anonNotFoundHtml.Should().Contain("Page Not Found");
        anonNotFoundHtml.Should().Contain("Go to Sign In");

        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");
        var authNotFound = await owner.GetAsync("/nonexistent-bakery-page");
        authNotFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var authNotFoundHtml = await authNotFound.Content.ReadAsStringAsync();
        authNotFoundHtml.Should().Contain("404");
        authNotFoundHtml.Should().Contain("Page Not Found");
        authNotFoundHtml.Should().Contain("Return to Dashboard");

        // REST API 404 must stay plain 404 without HTML page re-execution
        var apiNotFound = await owner.GetAsync("/api/products/999999");
        apiNotFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InteractiveCatalogAndPosUi_CreatesProductStocksStoreAndCompletesSale()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var prodName = $"UI Brioche {suffix}";

        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var owner = await db.Users.FirstAsync(u => u.UserName == "test_owner");

        var scopedAccessor = sp.GetRequiredService<ScopedUserAccessor>();
        scopedAccessor.Principal = BuildOwnerPrincipal(owner.Id, owner.UserName!, owner.FullName);

        using var ctx = BuildAuthorizedBunitContext(sp, owner.UserName!);

        // 1. ProductsPage interactive flow: empty submit handles DomainException gracefully, valid submit saves to DB
        var productsCut = ctx.RenderComponent<ProductsPage>();
        productsCut.WaitForAssertion(() =>
        {
            productsCut.Markup.Should().Contain("Add Product");
            productsCut.Markup.Should().NotContain("mud-skeleton");
            productsCut.Markup.Should().NotContain("mud-progress-linear");
        });

        await ClickButtonAsync(productsCut, "Add Product");
        productsCut.WaitForAssertion(() => productsCut.Markup.Should().Contain("New Product"));

        var prefilledProdSku = productsCut.FindComponents<MudTextField<string>>()
            .First(c => string.Equals(c.Instance.Label, "SKU", StringComparison.Ordinal))
            .Find("input")
            .GetAttribute("value");
        prefilledProdSku.Should().MatchRegex(@"^PRD-\d{4}$");

        // Submit blank Name -> must show error Snackbar instead of throwing unhandled exception
        await ClickButtonAsync(productsCut, "Save Product");
        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();
        snackbar.ShownSnackbars.Any(s => s.Severity == Severity.Error).Should().BeTrue();

        // Keep pre-filled SKU as-is; fill Name, Price and save
        await SetTextFieldAsync(productsCut, "Product Name", prodName);
        await SetNumericFieldAsync(productsCut, "Selling Price", "45");
        await ClickButtonAsync(productsCut, "Save Product");

        productsCut.WaitForAssertion(() =>
        {
            var errs = string.Join(" | ", snackbar.ShownSnackbars.Where(s => s.Severity == Severity.Error).Select(s => s.Message));
            productsCut.Markup.Should().NotContain("New Product", $"form should close after saving; snackbar errors: {errs}");
            productsCut.Markup.Should().Contain(prodName);
        }, TimeSpan.FromSeconds(5));

        await using var checkScope = factory.Services.CreateAsyncScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var savedProduct = await checkDb.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Name == prodName);
        savedProduct.Should().NotBeNull();
        savedProduct!.Sku.Should().Be(prefilledProdSku);
        savedProduct.SellingPrice.Should().Be(45.00m);

        // 2. RawMaterialsPage interactive flow (auto-filled SKU input)
        var materialsCut = ctx.RenderComponent<RawMaterialsPage>();
        materialsCut.WaitForAssertion(() =>
        {
            materialsCut.Markup.Should().Contain("Add Raw Material");
            materialsCut.Markup.Should().NotContain("mud-skeleton");
            materialsCut.Markup.Should().NotContain("mud-progress-linear");
        });
        await ClickButtonAsync(materialsCut, "Add Raw Material");
        materialsCut.WaitForAssertion(() => materialsCut.Markup.Should().Contain("Register Raw Material"));

        var prefilledMatSku = materialsCut.FindComponents<MudTextField<string>>()
            .First(c => string.Equals(c.Instance.Label, "SKU", StringComparison.Ordinal))
            .Find("input")
            .GetAttribute("value");
        prefilledMatSku.Should().MatchRegex(@"^RM-\d{4}$");

        var matName = $"UI Butter {suffix}";
        await SetTextFieldAsync(materialsCut, "Material Name", matName);
        await ClickButtonAsync(materialsCut, "Save Material");
        materialsCut.WaitForAssertion(() =>
        {
            materialsCut.Markup.Should().NotContain("Register Raw Material");
            materialsCut.Markup.Should().Contain(matName);
        }, TimeSpan.FromSeconds(5));

        var savedMaterial = await checkDb.RawMaterials.AsNoTracking().FirstOrDefaultAsync(m => m.Name == matName);
        savedMaterial.Should().NotBeNull();
        savedMaterial!.Sku.Should().Be(prefilledMatSku);

        // 3. Seed store stock via delivery flow and execute interactive POS checkout in PosTerminal
        var prodService = sp.GetRequiredService<IProductionService>();
        var delivService = sp.GetRequiredService<IDeliveryService>();

        await prodService.FinalizeProductionAsync(new FinalizeProductionRequest(
            null,
            savedProduct.Id,
            10m,
            10m,
            DateOnly.FromDateTime(DateTime.Today),
            null,
            "UI test batch",
            [],
            ConfirmNoRecipe: true,
            IdempotencyKey: $"UIBATCH-{suffix}"));

        var draft = await delivService.CreateDraftAsync(new CreateDeliveryRequest(
            "UI Driver",
            "Transfer to store",
            [new DeliveryItemInput(savedProduct.Id, 5m)]));
        await delivService.PrepareForDispatchAsync(draft.Id);
        await delivService.DispatchAsync(draft.Id);
        var loaded = await delivService.GetByIdAsync(draft.Id);
        await delivService.ConfirmReceivingAsync(
            draft.Id,
            new ConfirmDeliveryReceivingRequest("Full qty", [new ReceiveDeliveryItemInput(loaded.Items.Single().Id, 5m)]));

        var posCut = ctx.RenderComponent<PosTerminal>();
        posCut.WaitForAssertion(() =>
        {
            posCut.Markup.Should().Contain(prodName);
            posCut.Markup.Should().NotContain("mud-skeleton");
            posCut.Markup.Should().NotContain("mud-progress-linear");
        });

        // Click "Add to cart" on the matching product card
        await posCut.InvokeAsync(() =>
        {
            var productCard = posCut.FindAll(".mud-paper")
                .First(el => el.TextContent.Contains(prodName) && el.TextContent.Contains("Add to cart"));
            productCard.QuerySelector("button")!.Click();
        });

        posCut.WaitForAssertion(() =>
        {
            posCut.Markup.Should().Contain("Complete Sale &amp; Print Receipt");
            posCut.Markup.Should().Contain("45.00");
        });

        await ClickButtonAsync(posCut, "Complete Sale & Print Receipt");

        posCut.WaitForAssertion(() =>
            posCut.Markup.Should().Contain("Select bakery items to start checkout."));

        var storeQty = await checkDb.StoreInventories.AsNoTracking()
            .Where(s => s.ProductId == savedProduct.Id)
            .Select(s => s.CurrentQuantity)
            .SingleAsync();
        storeQty.Should().Be(4.0000m);
    }

    [Fact]
    public async Task ProductionPage_StartNewRecipeWithoutProductsShowsWarningWithoutCrashing()
    {
        var noProductsFactory = new StubProductService();

        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("owner1");
        auth.SetRoles(AppRoles.Owner);

        ctx.Services.AddSingleton<IProductService>(noProductsFactory);
        ctx.Services.AddSingleton<IProductionService>(new StubProductionService());
        ctx.Services.AddSingleton<IRecipeService>(new StubRecipeService());
        ctx.Services.AddSingleton<IRawMaterialService>(new StubRawMaterialService());

        ctx.RenderComponent<MudPopoverProvider>();
        var cut = ctx.RenderComponent<ProductionPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Recipes (BOM)");
            cut.Markup.Should().NotContain("mud-progress-linear");
        });

        // Switch to Recipes (BOM) tab and click Create / Update Recipe Version with 0 products
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("Recipes (BOM)")).Click());

        await ClickButtonAsync(cut, "Create / Update Recipe Version");

        var snackbar = ctx.Services.GetRequiredService<ISnackbar>();
        snackbar.ShownSnackbars.Any(s => s.Severity == Severity.Warning).Should().BeTrue();
    }

    private static ClaimsPrincipal BuildOwnerPrincipal(string id, string userName, string fullName) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Name, userName),
            new Claim("FullName", fullName),
            new Claim(ClaimTypes.Role, AppRoles.Owner)
        ], "TestAuth"));

    private static TestContext BuildAuthorizedBunitContext(IServiceProvider sp, string userName)
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized(userName);
        auth.SetRoles(AppRoles.Owner);
        auth.SetPolicies(
            AppPolicies.OwnerOnly, AppPolicies.ManagementOrOwner, AppPolicies.ManageCatalog, AppPolicies.ViewCatalog,
            AppPolicies.ManageSuppliers, AppPolicies.ViewSuppliers, AppPolicies.ManageRawMaterials, AppPolicies.ViewRawMaterials,
            AppPolicies.ReceiveRawMaterials, AppPolicies.ManageRecipes, AppPolicies.OperateProduction, AppPolicies.ViewFactoryInventory,
            AppPolicies.DispatchDeliveries, AppPolicies.ReceiveDeliveries, AppPolicies.ViewStoreInventory, AppPolicies.OperatePos,
            AppPolicies.RecordWasteOrReturn, AppPolicies.ApproveAdjustmentsAndVariances, AppPolicies.SubmitDailyClosing,
            AppPolicies.ViewAuditLogs, AppPolicies.ViewReports, AppPolicies.ManageUsers, AppPolicies.ManageSettings);

        ctx.Services.AddSingleton(sp.GetRequiredService<IProductService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ICategoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRawMaterialService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISupplierService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IInventoryLedgerService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IProductionService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRecipeService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IPosService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISystemSettingService>());

        ctx.RenderComponent<MudPopoverProvider>();
        ctx.RenderComponent<MudDialogProvider>();
        return ctx;
    }

    private static Task ClickButtonAsync(IRenderedFragment cut, string buttonText) =>
        cut.InvokeAsync(() =>
        {
            var btn = cut.FindAll("button").First(b => b.TextContent.Contains(buttonText, StringComparison.OrdinalIgnoreCase));
            btn.Click();
        });

    private static Task SetTextFieldAsync(IRenderedFragment cut, string label, string value) =>
        cut.InvokeAsync(() =>
        {
            var field = cut.FindComponents<MudTextField<string>>().First(c => string.Equals(c.Instance.Label, label, StringComparison.Ordinal));
            field.Find("input").Change(value);
        });

    private static Task SetNumericFieldAsync(IRenderedFragment cut, string label, string value) =>
        cut.InvokeAsync(() =>
        {
            var field = cut.FindComponents<MudNumericField<decimal>>().First(c => string.Equals(c.Instance.Label, label, StringComparison.Ordinal));
            field.Find("input").Change(value);
        });

    private sealed class StubProductService : IProductService
    {
        public Task<PagedResult<ProductDto>> GetPagedAsync(ProductQuery query, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<ProductDto>([], 0, 1, 200));

        public Task<ProductDto> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetNextSkuAsync(CancellationToken ct = default) => Task.FromResult("PRD-0001");
        public Task<ProductDto> SaveAsync(SaveProductRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetArchiveStatusAsync(int productId, bool archive, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<UnitOfMeasureDto>> GetUnitsOfMeasureAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UnitOfMeasureDto>>([new(1, "pcs", "Pieces", "Count", 1m, true)]);
    }

    private sealed class StubRawMaterialService : IRawMaterialService
    {
        public Task<PagedResult<RawMaterialDto>> GetPagedAsync(RawMaterialQuery query, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<RawMaterialDto>(
                [new(1, "RM-1", "Flour", "", 2, "kg", "Weight", null, 10m, 5m, null, null, "In Stock", false, DateTime.UtcNow)], 1, 1, 200));

        public Task<RawMaterialDto> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetNextSkuAsync(CancellationToken ct = default) => Task.FromResult("RM-0001");
        public Task<RawMaterialDto> SaveAsync(SaveRawMaterialRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetArchiveStatusAsync(int materialId, bool archive, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InventoryMovementDto> AdjustStockAsync(RawMaterialAdjustmentRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubProductionService : IProductionService
    {
        public Task<PagedResult<ProductionBatchDto>> GetPagedAsync(ProductionQuery query, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<ProductionBatchDto>([], 0, 1, 50));

        public Task<ProductionBatchDto> GetByIdAsync(long id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProductionBatchDto> CreatePlanAsync(CreateProductionPlanRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProductionBatchDto> FinalizeProductionAsync(FinalizeProductionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelPlanAsync(long batchId, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubRecipeService : IRecipeService
    {
        public Task<RecipeDto?> GetActiveByProductAsync(int productId, CancellationToken ct = default) => Task.FromResult<RecipeDto?>(null);
        public Task<IReadOnlyList<RecipeDto>> GetHistoryByProductAsync(int productId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RecipeDto>>([]);
        public Task<RecipeDto> SaveNewVersionAsync(SaveRecipeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProductionRequirementPreviewDto> CalculateRequirementsAsync(int productId, decimal targetQuantity, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
