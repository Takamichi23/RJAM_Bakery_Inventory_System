using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class AuthenticationAndRbacTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task UnauthenticatedApiRequests_Return401Unauthorized()
    {
        using var client = factory.CreateAnonymousClient();

        (await client.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/raw-materials")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/ledger/movements")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/audit-logs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_Returns401()
    {
        using var client = factory.CreateAnonymousClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { UserName = "test_owner", Password = "WrongPassword1!" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RbacEnforcement_BlocksUnauthorizedRolesOnBackendEndpoints()
    {
        using var baker = await factory.CreateAuthenticatedClientAsync("test_baker");
        using var cashier = await factory.CreateAuthenticatedClientAsync("test_cashier");
        using var manager = await factory.CreateAuthenticatedClientAsync("test_manager");

        // Baker: allowed raw materials view, denied catalog writes, adjustments, audit logs, users, settings
        (await baker.GetAsync("/api/raw-materials")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await baker.PostAsJsonAsync("/api/categories", new SaveCategoryRequest(null, "UnauthCat", "", 10))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.PostAsJsonAsync("/api/raw-materials/1/adjust", new RawMaterialAdjustmentRequest(1, 5m, 2, "Baker try"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.GetAsync("/api/audit-logs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await baker.PutAsJsonAsync("/api/settings", new UpdateSystemSettingRequest("BakeryName", "Hacked"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Cashier: allowed catalog view, denied raw materials, audit logs, users, catalog writes
        (await cashier.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await cashier.GetAsync("/api/raw-materials")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/audit-logs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.PostAsJsonAsync("/api/categories", new SaveCategoryRequest(null, "CashierCat", "", 11))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager: allowed raw materials, audit logs, ledger verify; denied user management & settings & catalog writes
        (await manager.GetAsync("/api/raw-materials")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync("/api/audit-logs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync("/api/ledger/verify")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PutAsJsonAsync("/api/settings", new UpdateSystemSettingRequest("BakeryName", "MgrTry"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync("/api/categories", new SaveCategoryRequest(null, "MgrCat", "", 12))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OwnerUserLifecycle_DeactivatedUserCannotLoginOrKeepSession()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        var createResp = await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(
            "temp_cashier",
            "Temp Cashier",
            "temp.cashier@rjam.test",
            AppRoles.Cashier,
            "TempPass#2026",
            MustChangePassword: true));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdUser = await createResp.Content.ReadFromJsonAsync<UserDto>();
        createdUser.Should().NotBeNull();

        using var tempClient = await factory.CreateAuthenticatedClientAsync("temp_cashier", "TempPass#2026");
        (await tempClient.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Owner deactivates the user
        var deactivateResp = await owner.PatchAsJsonAsync($"/api/users/{createdUser!.Id}/active", new { IsActive = false });
        deactivateResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Existing cookie session must immediately fail validation
        (await tempClient.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Fresh login must fail
        using var retryClient = factory.CreateAnonymousClient();
        var loginRetry = await retryClient.PostAsJsonAsync("/api/auth/login", new { UserName = "temp_cashier", Password = "TempPass#2026" });
        loginRetry.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Owner resets password & reactivates
        var resetResp = await owner.PostAsJsonAsync($"/api/users/{createdUser.Id}/reset-password",
            new ResetPasswordRequest(createdUser.Id, "NewPass#2026", true));
        resetResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var reactivateResp = await owner.PatchAsJsonAsync($"/api/users/{createdUser.Id}/active", new { IsActive = true });
        reactivateResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var newSession = await factory.CreateAuthenticatedClientAsync("temp_cashier", "NewPass#2026");
        (await newSession.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
