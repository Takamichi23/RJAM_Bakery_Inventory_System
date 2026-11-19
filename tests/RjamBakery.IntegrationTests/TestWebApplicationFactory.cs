using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using RjamBakery.Domain.Common;
using RjamBakery.Domain.Entities;

namespace RjamBakery.IntegrationTests;

[CollectionDefinition("BakeryIntegration")]
public sealed class BakeryIntegrationCollection : ICollectionFixture<TestWebApplicationFactory>;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestConnectionString =
        "Server=127.0.0.1;Port=3306;Database=rjam_bakery_ms_test_db;User=root;Password=;";

    public const string DefaultTestPassword = "TestUser#2026";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", TestConnectionString);
        builder.UseSetting("Security:AuthPermitLimitPerMinute", "500");
        builder.UseSetting("Security:ApiPermitLimitPerMinute", "5000");
        builder.UseSetting("Security:ExportPermitLimitPerMinute", "200");
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new MySqlConnection("Server=127.0.0.1;Port=3306;User=root;Password=;"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP DATABASE IF EXISTS `rjam_bakery_ms_test_db`;";
            await cmd.ExecuteNonQueryAsync();
        }

        // Trigger host build + Program.cs MigrateAsync & SeedReferenceDefaultsAsync
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await EnsureTestUserAsync(userManager, "test_owner", "Owner Test", "owner@rjam.test", AppRoles.Owner);
        await EnsureTestUserAsync(userManager, "test_manager", "Manager Test", "manager@rjam.test", AppRoles.Manager);
        await EnsureTestUserAsync(userManager, "test_baker", "Baker Test", "baker@rjam.test", AppRoles.Baker);
        await EnsureTestUserAsync(userManager, "test_cashier", "Cashier Test", "cashier@rjam.test", AppRoles.Cashier);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }

    public HttpClient CreateAnonymousClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string userName, string password = DefaultTestPassword)
    {
        var client = CreateAnonymousClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { UserName = userName, Password = password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task EnsureTestUserAsync(
        UserManager<ApplicationUser> userManager,
        string userName,
        string fullName,
        string email,
        string role)
    {
        var existing = await userManager.FindByNameAsync(userName);
        if (existing is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = userName,
            FullName = fullName,
            Email = email,
            EmailConfirmed = true,
            IsActive = true
        };

        var create = await userManager.CreateAsync(user, DefaultTestPassword);
        if (!create.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", create.Errors.Select(e => e.Description)));
        }

        var addRole = await userManager.AddToRoleAsync(user, role);
        if (!addRole.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", addRole.Errors.Select(e => e.Description)));
        }
    }
}
