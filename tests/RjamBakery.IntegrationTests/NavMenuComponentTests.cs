using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using MudBlazor.Services;
using RjamBakery.Domain.Common;
using RjamBakery.Web.Components.Layout;

namespace RjamBakery.IntegrationTests;

public sealed class NavMenuComponentTests : TestContext
{
    public NavMenuComponentTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void NavMenu_ForBaker_ShowsFactoryAndHidesPosAndAdmin()
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("baker1");
        auth.SetRoles(AppRoles.Baker);

        var cut = RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("Raw Materials");
        cut.Markup.Should().Contain("Production");
        cut.Markup.Should().NotContain("href=\"/pos\"");
        cut.Markup.Should().NotContain("User Management");
    }

    [Fact]
    public void NavMenu_ForCashier_ShowsPosAndStoreAndHidesFactoryAndAdmin()
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("cashier1");
        auth.SetRoles(AppRoles.Cashier);

        var cut = RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("href=\"/pos\"");
        cut.Markup.Should().Contain("Sales History");
        cut.Markup.Should().Contain("Store Inventory");
        cut.Markup.Should().NotContain("Raw Materials");
        cut.Markup.Should().NotContain("Production");
        cut.Markup.Should().NotContain("User Management");
    }

    [Fact]
    public void NavMenu_ForOwner_ShowsAdministrationSection()
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("owner1");
        auth.SetRoles(AppRoles.Owner);

        var cut = RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("User Management");
        cut.Markup.Should().Contain("Settings");
        cut.Markup.Should().Contain("Reports");
    }
}
