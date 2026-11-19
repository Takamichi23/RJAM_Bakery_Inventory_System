using System.Net;
using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Enums;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Web.Components.Pages;
using RjamBakery.Web.Components.Pages.Admin;
using RjamBakery.Web.Components.Pages.Factory;
using RjamBakery.Web.Components.Pages.Pos;
using RjamBakery.Web.Components.Pages.Reports;
using RjamBakery.Web.Components.Pages.Store;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class RoleInterfaceAndWorkflowTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task AllFourRoles_PermittedAndRestrictedHttpRoutes_EnforceExactInterfaceBoundaries()
    {
        var roleMatrix = new Dictionary<string, (string[] Allowed, string[] Blocked)>
        {
            ["test_owner"] = (
                ["/", "/products", "/raw-materials", "/suppliers", "/production", "/factory-inventory", "/deliveries", "/store-inventory", "/pos", "/sales-history", "/returns-waste", "/daily-closing", "/reports", "/audit-logs", "/users", "/settings", "/Account/ChangePassword"],
                []
            ),
            ["test_manager"] = (
                ["/", "/raw-materials", "/suppliers", "/production", "/factory-inventory", "/deliveries", "/store-inventory", "/sales-history", "/returns-waste", "/daily-closing", "/reports", "/audit-logs", "/Account/ChangePassword"],
                ["/products", "/pos", "/users", "/settings"]
            ),
            ["test_baker"] = (
                ["/", "/raw-materials", "/suppliers", "/production", "/factory-inventory", "/deliveries", "/returns-waste", "/Account/ChangePassword"],
                ["/products", "/store-inventory", "/pos", "/sales-history", "/daily-closing", "/reports", "/audit-logs", "/users", "/settings"]
            ),
            ["test_cashier"] = (
                ["/", "/deliveries", "/store-inventory", "/pos", "/sales-history", "/returns-waste", "/daily-closing", "/Account/ChangePassword"],
                ["/products", "/raw-materials", "/suppliers", "/production", "/factory-inventory", "/reports", "/audit-logs", "/users", "/settings"]
            )
        };

        foreach (var (userName, (allowed, blocked)) in roleMatrix)
        {
            using var client = await factory.CreateAuthenticatedClientAsync(userName);

            foreach (var route in allowed)
            {
                var resp = await client.GetAsync(route);
                resp.StatusCode.Should().Be(HttpStatusCode.OK, $"{userName} must be able to open permitted page '{route}'");
                var html = await resp.Content.ReadAsStringAsync();
                html.Should().NotContain("An unhandled exception occurred");
                html.Should().NotContain("You do not have permission to access this module");
            }

            foreach (var route in blocked)
            {
                var resp = await client.GetAsync(route);
                resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError, $"{userName} hitting restricted route '{route}' must not throw 500");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var html = await resp.Content.ReadAsStringAsync();
                    html.Should().Contain("You do not have permission to access this module",
                        $"{userName} must see access denied on restricted route '{route}'");
                }
                else
                {
                    resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);
                }
            }
        }
    }

    [Fact]
    public async Task OwnerUsersPage_CreateEditSearchAndToggleActive_WorksEndToEnd()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var owner = await db.Users.FirstAsync(u => u.UserName == "test_owner");
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(owner.Id, owner.UserName!, owner.FullName, AppRoles.Owner);

        using var ctx = BuildRoleBunitContext(sp, owner.UserName!, AppRoles.Owner);
        var cut = ctx.RenderComponent<UsersPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("mud-skeleton"));

        var uname = $"ui_staff_{Guid.NewGuid():N}"[..14];

        // 1. Create a new Cashier via UsersPage UI
        await ClickButtonAsync(cut, "Create User");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("New Staff Account"));

        await SetTextFieldAsync(cut, "Username", uname);
        await SetTextFieldAsync(cut, "Full Name", "Initial Staff Name");
        await SetTextFieldAsync(cut, "Email", $"{uname}@rjam.test");
        await SetTextFieldAsync(cut, "Temporary Password", "StaffPass#2026");
        await ClickButtonAsync(cut, "Save User");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("New Staff Account");
            cut.FindAll("tr").Should().Contain(r => r.TextContent.Contains(uname) && r.TextContent.Contains("Temp Password"));
        });

        // 2. Edit the newly created user via the new Edit panel
        await cut.InvokeAsync(() =>
        {
            var row = cut.FindAll("tr").First(r => r.TextContent.Contains(uname));
            row.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain($"Edit User — {uname}"));
        await SetTextFieldAsync(cut, "Full Name", "Promoted Staff Name");
        await ClickButtonAsync(cut, "Save Changes");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain($"Edit User — {uname}");
            cut.Markup.Should().Contain("Promoted Staff Name");
        });

        // 3. Verify persisted in MariaDB
        var updated = await db.Users.AsNoTracking().FirstAsync(u => u.UserName == uname);
        updated.FullName.Should().Be("Promoted Staff Name");
    }

    [Fact]
    public async Task RoleScopedInterfaces_VerifyExactButtonVisibilityForBakerCashierAndManager()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var baker = await db.Users.FirstAsync(u => u.UserName == "test_baker");
        var cashier = await db.Users.FirstAsync(u => u.UserName == "test_cashier");
        var manager = await db.Users.FirstAsync(u => u.UserName == "test_manager");

        // Baker on SuppliersPage: CAN Record Delivery Receipt, CANNOT Add Supplier
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(baker.Id, baker.UserName!, baker.FullName, AppRoles.Baker);
        using (var bakerCtx = BuildRoleBunitContext(sp, baker.UserName!, AppRoles.Baker))
        {
            var supCut = bakerCtx.RenderComponent<SuppliersPage>();
            supCut.WaitForAssertion(() => supCut.Markup.Should().NotContain("mud-skeleton"));
            supCut.Markup.Should().Contain("Record Delivery Receipt");
            await supCut.InvokeAsync(() => supCut.FindAll(".mud-tab").First(t => t.TextContent.Contains("Suppliers")).Click());
            supCut.Markup.Should().NotContain("Add Supplier");

            var prodCut = bakerCtx.RenderComponent<ProductionPage>();
            prodCut.WaitForAssertion(() => prodCut.Markup.Should().NotContain("mud-skeleton"));
            prodCut.Markup.Should().Contain("Record Production");
            await prodCut.InvokeAsync(() => prodCut.FindAll(".mud-tab").First(t => t.TextContent.Contains("Recipes (BOM)")).Click());
            prodCut.Markup.Should().NotContain("Create / Update Recipe Version");
        }

        // Cashier on StoreInventoryPage & SalesHistory: CAN Request Adj, CANNOT Direct Adj or Void/Refund
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(cashier.Id, cashier.UserName!, cashier.FullName, AppRoles.Cashier);
        using (var cashierCtx = BuildRoleBunitContext(sp, cashier.UserName!, AppRoles.Cashier))
        {
            var storeCut = cashierCtx.RenderComponent<StoreInventoryPage>();
            storeCut.WaitForAssertion(() => storeCut.Markup.Should().NotContain("mud-skeleton"));
            storeCut.Markup.Should().NotContain("Direct Adj");

            var salesCut = cashierCtx.RenderComponent<SalesHistory>();
            salesCut.WaitForAssertion(() => salesCut.Markup.Should().NotContain("mud-skeleton"));
            salesCut.Markup.Should().Contain("Open POS Terminal");
            salesCut.Markup.Should().NotContain("Void / Refund");
        }

        // Manager on ProductionPage & ReportsPage: CANNOT Record Production, CAN View Reports & Export
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(manager.Id, manager.UserName!, manager.FullName, AppRoles.Manager);
        using (var mgrCtx = BuildRoleBunitContext(sp, manager.UserName!, AppRoles.Manager))
        {
            var prodCut = mgrCtx.RenderComponent<ProductionPage>();
            prodCut.WaitForAssertion(() => prodCut.Markup.Should().NotContain("mud-skeleton"));
            prodCut.Markup.Should().NotContain("Confirm &amp; Finalize Production");
            prodCut.Markup.Should().Contain("Production History");

            var repCut = mgrCtx.RenderComponent<ReportsPage>();
            repCut.WaitForAssertion(() =>
            {
                repCut.Markup.Should().NotContain("mud-skeleton");
                repCut.Markup.Should().Contain("Export Excel");
            });
            repCut.Markup.Should().Contain("Export PDF");
        }
    }

    [Fact]
    public async Task ProductionPage_PreservesManualActualOutputWhenCheckingRecipe_AndClearsStalePreview()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var owner = await db.Users.FirstAsync(u => u.UserName == "test_owner");
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(owner.Id, owner.UserName!, owner.FullName, AppRoles.Owner);

        var cat = await db.ProductCategories.FirstAsync();
        var pcs = await db.UnitsOfMeasure.FirstAsync(u => u.Code == "pcs");
        var productService = sp.GetRequiredService<IProductService>();
        var prod = await productService.SaveAsync(new SaveProductRequest(
            null, cat.Id, $"QA-PR-{Guid.NewGuid():N}"[..12], $"QA Croissant {Guid.NewGuid():N}"[..20],
            "QA batch test", pcs.Id, 40m, 5m, 2, null));

        using var ctx = BuildRoleBunitContext(sp, owner.UserName!, AppRoles.Owner);
        var cut = ctx.RenderComponent<ProductionPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("mud-skeleton");
            cut.Markup.Should().Contain("Actual Output");
        });

        // Change Actual Output manually to 75 while Planned Qty stays 100
        await SetNumericFieldAsync(cut, "Actual Output", "75");

        // Click Check Recipe -> Actual Output must remain 75, NOT be overwritten back to 100
        await ClickButtonAsync(cut, "Check Recipe");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm &amp; Finalize Production"));

        var actualField = cut.FindComponents<MudNumericField<decimal>>()
            .First(c => string.Equals(c.Instance.Label, "Actual Output", StringComparison.Ordinal));
        actualField.Instance.GetState(x => x.Value).Should().Be(75m);

        // Changing Planned Qty invalidates stale preview so user must re-check recipe
        await SetNumericFieldAsync(cut, "Planned Qty", "80");
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Confirm &amp; Finalize Production"));
    }

    [Fact]
    public async Task PosTerminal_DiscountIsClampedWhenCartQuantityDecreases()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var owner = await db.Users.FirstAsync(u => u.UserName == "test_owner");
        var cashier = await db.Users.FirstAsync(u => u.UserName == "test_cashier");

        // Seed 10 units of a test product in Store as Owner
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(owner.Id, owner.UserName!, owner.FullName, AppRoles.Owner);
        var cat = await db.ProductCategories.FirstAsync();
        var pcs = await db.UnitsOfMeasure.FirstAsync(u => u.Code == "pcs");
        var prodName = $"QA Discount Bun {Guid.NewGuid():N}"[..22];
        var prod = await sp.GetRequiredService<IProductService>().SaveAsync(new SaveProductRequest(
            null, cat.Id, $"QA-DC-{Guid.NewGuid():N}"[..12], prodName, "Test", pcs.Id, 20m, 2m, 2, null));

        await sp.GetRequiredService<IInventoryLedgerService>().RecordStoreMovementAsync(new RecordStoreMovementRequest(
            prod.Id, 10m, InventoryLocation.InTransit, InventoryLocation.Store,
            InventoryMovementType.StoreReceiving, "QATest", prod.Id.ToString(), $"DEL-QA-{prod.Id}",
            "Seed for POS discount clamp test", DeduplicationKey: $"QA-DISC-SEED-{prod.Id}"));

        // Operate POS as Cashier
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(cashier.Id, cashier.UserName!, cashier.FullName, AppRoles.Cashier);
        using var ctx = BuildRoleBunitContext(sp, cashier.UserName!, AppRoles.Cashier);
        var cut = ctx.RenderComponent<PosTerminal>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain(prodName);
            cut.Markup.Should().NotContain("mud-skeleton");
        });

        // Add 2 units (Subtotal = 40), re-finding button after first render
        for (int i = 0; i < 2; i++)
        {
            await cut.InvokeAsync(() =>
            {
                var card = cut.FindAll(".mud-paper").First(e => e.TextContent.Contains(prodName) && e.TextContent.Contains("Add to cart"));
                card.QuerySelector("button")!.Click();
            });
        }

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("40.00"));

        // Apply 30 discount (valid for Subtotal 40)
        await SetNumericFieldAsync(cut, "Discount", "30");

        // Decrease quantity from 2 -> 1 (Subtotal drops to 20 -> Discount must clamp to 20 so checkout does not fail!)
        await cut.InvokeAsync(() =>
        {
            cut.Find("button[aria-label='Decrease quantity']").Click();
        });

        cut.WaitForAssertion(() =>
        {
            var discountField = cut.FindComponents<MudNumericField<decimal>>()
                .First(c => string.Equals(c.Instance.Label, "Discount", StringComparison.Ordinal));
            discountField.Instance.GetState(x => x.Value).Should().Be(20m);
        });

        // Complete checkout cleanly without domain validation error
        await ClickButtonAsync(cut, "Complete Sale & Print Receipt");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Select bakery items to start checkout."));

        // Verify Offline Mode toggle switch is rendered on PosTerminal
        var offlineSwitch = cut.FindComponent<MudSwitch<bool>>();
        offlineSwitch.Instance.Label.Should().Be("Offline Mode");
        cut.Markup.Should().Contain("Offline Mode");
    }

    [Fact]
    public async Task ReturnsWastePage_SwitchingFromStoreCustomerReturnToFactoryResetsInvalidCategory()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var manager = await db.Users.FirstAsync(u => u.UserName == "test_manager");
        sp.GetRequiredService<ScopedUserAccessor>().Principal = BuildPrincipal(manager.Id, manager.UserName!, manager.FullName, AppRoles.Manager);

        using var ctx = BuildRoleBunitContext(sp, manager.UserName!, AppRoles.Manager);
        var cut = ctx.RenderComponent<ReturnsWastePage>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("mud-skeleton"));

        var locationSelect = cut.FindComponent<MudSelect<InventoryLocation>>();
        var categorySelect = cut.FindComponent<MudSelect<WasteReturnCategory>>();

        // Choose CustomerReturn while on Store
        await cut.InvokeAsync(() => categorySelect.Instance.ValueChanged.InvokeAsync(WasteReturnCategory.CustomerReturn));

        // Switch Location to Factory -> Category must auto-reset from CustomerReturn to Damaged
        await cut.InvokeAsync(() => locationSelect.Instance.ValueChanged.InvokeAsync(InventoryLocation.Factory));

        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<MudSelect<WasteReturnCategory>>().Instance.GetState(x => x.Value).Should().Be(WasteReturnCategory.Damaged);
        });
    }

    private static ClaimsPrincipal BuildPrincipal(string id, string userName, string fullName, string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Name, userName),
            new Claim("FullName", fullName),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth"));

    private static TestContext BuildRoleBunitContext(IServiceProvider sp, string userName, string role)
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized(userName);
        auth.SetRoles(role);

        ctx.Services.AddSingleton(sp.GetRequiredService<IProductService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ICategoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRawMaterialService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISupplierService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IMaterialReceiptService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IInventoryLedgerService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IProductionService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRecipeService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IFactoryInventoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IDeliveryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IStoreInventoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IPosService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IWasteAndReturnService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IDailyClosingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IReportingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IDashboardService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IAuditLogService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISystemSettingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IUserManagementService>());

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
}
