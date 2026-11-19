using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Infrastructure.Persistence;
using RjamBakery.Web.Components.Pages;
using RjamBakery.Web.Components.Pages.Admin;
using RjamBakery.Web.Components.Pages.Catalog;
using RjamBakery.Web.Components.Pages.Factory;
using RjamBakery.Web.Components.Pages.Pos;
using RjamBakery.Web.Components.Pages.Reports;
using RjamBakery.Web.Components.Pages.Store;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class AllPagesRenderTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task AllOperationalAndAdminPages_RenderWithLiveServicesAndZeroExceptions()
    {
        string ownerId;
        string ownerName;
        string ownerFullName;

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await db.Users.FirstAsync(u => u.UserName == "test_owner");
            ownerId = owner.Id;
            ownerName = owner.UserName!;
            ownerFullName = owner.FullName;
        }

        await AssertPageRendersAsync<Home>(ownerId, ownerName, ownerFullName, "Dashboard");
        await AssertPageRendersAsync<ProductsPage>(ownerId, ownerName, ownerFullName, "Products");
        await AssertPageRendersAsync<RawMaterialsPage>(ownerId, ownerName, ownerFullName, "Raw Materials Inventory");
        await AssertPageRendersAsync<SuppliersPage>(ownerId, ownerName, ownerFullName, "Suppliers");
        await AssertPageRendersAsync<ProductionPage>(ownerId, ownerName, ownerFullName, "Bakery Production");
        await AssertPageRendersAsync<FactoryInventoryPage>(ownerId, ownerName, ownerFullName, "Factory Finished Goods Inventory");
        await AssertPageRendersAsync<DeliveriesPage>(ownerId, ownerName, ownerFullName, "Factory-to-Store Deliveries");
        await AssertPageRendersAsync<StoreInventoryPage>(ownerId, ownerName, ownerFullName, "Store Inventory Management");
        await AssertPageRendersAsync<PosTerminal>(ownerId, ownerName, ownerFullName, "Point of Sale");
        await AssertPageRendersAsync<SalesHistory>(ownerId, ownerName, ownerFullName, "POS Sales History");
        await AssertPageRendersAsync<ReturnsWastePage>(ownerId, ownerName, ownerFullName, "Returns, Damages");
        await AssertPageRendersAsync<DailyClosingPage>(ownerId, ownerName, ownerFullName, "Daily Closing");
        await AssertPageRendersAsync<ReportsPage>(ownerId, ownerName, ownerFullName, "Operational Reports");
        await AssertPageRendersAsync<UsersPage>(ownerId, ownerName, ownerFullName, "User Management");
        await AssertPageRendersAsync<AuditLogsPage>(ownerId, ownerName, ownerFullName, "System Audit Logs");
        await AssertPageRendersAsync<SettingsPage>(ownerId, ownerName, ownerFullName, "System Settings");
    }

    private async Task AssertPageRendersAsync<TPage>(
        string userId,
        string userName,
        string fullName,
        string expectedHeading)
        where TPage : IComponent
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var scopedAccessor = sp.GetRequiredService<ScopedUserAccessor>();
        scopedAccessor.Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userName),
            new Claim("FullName", fullName),
            new Claim(ClaimTypes.Role, AppRoles.Owner)
        ], "TestAuth"));

        using var ctx = new TestContext();
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

        ctx.Services.AddSingleton(sp.GetRequiredService<IDashboardService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IProductService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ICategoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRawMaterialService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISupplierService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IMaterialReceiptService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IRecipeService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IProductionService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IFactoryInventoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IDeliveryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IStoreInventoryService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IPosService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IWasteAndReturnService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IDailyClosingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IReportingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IInventoryLedgerService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IAuditLogService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<ISystemSettingService>());
        ctx.Services.AddSingleton(sp.GetRequiredService<IUserManagementService>());

        ctx.RenderComponent<MudPopoverProvider>();
        var cut = ctx.RenderComponent<TPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain(expectedHeading);
            cut.Markup.Should().NotContain("mud-progress-linear");
            cut.Markup.Should().NotContain("mud-skeleton");
        }, TimeSpan.FromSeconds(10));
    }
}
