using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using RjamBakery.Application.Contracts;
using RjamBakery.Application.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Infrastructure.Identity;
using RjamBakery.Web.Components.Layout;
using RjamBakery.Web.Components.Pages;
using RjamBakery.Web.Components.Shared;

namespace RjamBakery.IntegrationTests;

public sealed class Phase7UiTests : TestContext
{
    public Phase7UiTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void HomeDashboard_TailorsSectionsForOwnerBakerAndCashier()
    {
        Services.AddSingleton<IDashboardService>(new StubDashboardService());
        var auth = this.AddTestAuthorization();

        // Owner sees Sales, Production, Discrepancies, and Actions
        auth.SetAuthorized("owner1");
        auth.SetRoles(AppRoles.Owner);
        var ownerCut = RenderComponent<Home>();
        ownerCut.Markup.Should().Contain("TODAY'S NET SALES");
        ownerCut.Markup.Should().Contain("TODAY'S PRODUCTION");
        ownerCut.Markup.Should().Contain("INVENTORY DISCREPANCIES");

        // Baker sees Production & Raw Materials, hides Sales & Discrepancies
        auth.SetAuthorized("baker1");
        auth.SetRoles(AppRoles.Baker);
        var bakerCut = RenderComponent<Home>();
        bakerCut.Markup.Should().Contain("TODAY'S PRODUCTION");
        bakerCut.Markup.Should().Contain("RAW MATERIALS AVAILABLE");
        bakerCut.Markup.Should().NotContain("TODAY'S NET SALES");
        bakerCut.Markup.Should().NotContain("INVENTORY DISCREPANCIES");

        // Cashier sees Store Sellable Stock, Sales, and Open POS, hides Raw Materials & Production
        auth.SetAuthorized("cashier1");
        auth.SetRoles(AppRoles.Cashier);
        var cashierCut = RenderComponent<Home>();
        cashierCut.Markup.Should().Contain("TODAY'S NET SALES");
        cashierCut.Markup.Should().Contain("STORE SELLABLE STOCK");
        cashierCut.Markup.Should().Contain("Open POS");
        cashierCut.Markup.Should().NotContain("RAW MATERIALS AVAILABLE");
        cashierCut.Markup.Should().NotContain("TODAY'S PRODUCTION");
    }

    [Fact]
    public void NavMenu_ForManager_ShowsOperationalAndReportsAndHidesAdminOnlyLinks()
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("mgr1");
        auth.SetRoles(AppRoles.Manager);

        var cut = RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("Reports");
        cut.Markup.Should().Contain("Audit Logs");
        cut.Markup.Should().Contain("Daily Closing");
        cut.Markup.Should().NotContain("User Management");
        cut.Markup.Should().NotContain("Settings");
        cut.Markup.Should().NotContain("href=\"/pos\"");
    }

    [Fact]
    public void MainLayout_PreservesAppBarClearanceAndScrollableSidebar()
    {
        Services.AddScoped<ScopedUserAccessor>();
        Services.AddSingleton<IDashboardService>(new StubDashboardService());
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("owner1");
        auth.SetRoles(AppRoles.Owner);

        var cut = RenderComponent<MainLayout>();

        var main = cut.Find(".mud-main-content");
        main.ClassList.Should().NotContain("pa-4");
        main.ClassList.Should().NotContain("pa-md-6");

        var container = cut.Find(".mud-main-content > .mud-container");
        container.ClassList.Should().Contain("pa-3");
        container.ClassList.Should().Contain("pa-sm-4");
        container.ClassList.Should().Contain("pa-md-6");

        cut.Find(".rjam-drawer-scroll").InnerHtml.Should().Contain("rjam-nav");
        cut.Markup.Should().Contain("images/rjam-logo-light.png");
        cut.Markup.Should().Contain("images/rjam-mark.png");
        cut.Markup.Should().Contain("Settings");
    }

    [Fact]
    public async Task HomeDashboard_ShowsSkeletonWhileLoadingAndClearsWhenReady()
    {
        var tcs = new TaskCompletionSource<DashboardSummaryDto>();
        Services.AddSingleton<IDashboardService>(new DeferredDashboardService(tcs.Task));
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("owner1");
        auth.SetRoles(AppRoles.Owner);

        var cut = RenderComponent<Home>();
        cut.Markup.Should().Contain("mud-skeleton");

        tcs.SetResult(await new StubDashboardService().GetSummaryAsync());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("mud-skeleton");
            cut.Markup.Should().Contain("TODAY'S NET SALES");
        });
    }

    [Fact]
    public void LoadingButtonContent_And_NotFoundScreen_RenderExpectedStates()
    {
        var idle = RenderComponent<LoadingButtonContent>(p => p
            .Add(x => x.Loading, false)
            .Add(x => x.Text, "Save Product")
            .Add(x => x.LoadingText, "Saving..."));
        idle.Markup.Should().Contain("Save Product");
        idle.Markup.Should().NotContain("mud-progress-circular");

        var busy = RenderComponent<LoadingButtonContent>(p => p
            .Add(x => x.Loading, true)
            .Add(x => x.Text, "Save Product")
            .Add(x => x.LoadingText, "Saving..."));
        busy.Markup.Should().Contain("Saving...");
        busy.Markup.Should().Contain("mud-progress-circular");

        var nf = RenderComponent<NotFoundScreen>(p => p.Add(x => x.IsAuthenticated, true));
        nf.Markup.Should().Contain("404");
        nf.Markup.Should().Contain("Page Not Found");
        nf.Markup.Should().Contain("Return to Dashboard");
    }

    private sealed class DeferredDashboardService(Task<DashboardSummaryDto> task) : IDashboardService
    {
        public Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default) => task;
    }

    private sealed class StubDashboardService : IDashboardService
    {
        public Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new DashboardSummaryDto(
                AppRoles.Owner,
                "RJAM Bakery",
                "₱",
                12,
                145.5m,
                210m,
                95m,
                2,
                1,
                180m,
                3,
                1,
                1,
                4250.00m,
                28,
                1,
                1,
                1,
                0,
                [new DashboardAlertDto("Warning", "Low Stock", "Bread flour below threshold.", "/raw-materials")],
                [new RecentActivityDto(DateTime.UtcNow, "MOV-1", "Pandesal", "PosSale", -5m, "pcs", "RCP-1", "Cashier")]));
    }
}
