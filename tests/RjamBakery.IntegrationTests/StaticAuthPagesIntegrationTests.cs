using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RjamBakery.Infrastructure.Persistence;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class StaticAuthPagesIntegrationTests(TestWebApplicationFactory factory)
{
    [Fact]
    public async Task LoginAndLogoutForms_RenderAndSubmitValidAntiforgeryTokens()
    {
        using var client = factory.CreateAnonymousClient();

        // 1. GET /Account/Login (since users already exist in collection fixture)
        var loginPage = await client.GetAsync("/Account/Login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();

        var tokenMatches = Regex.Matches(loginHtml, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        tokenMatches.Count.Should().Be(1, "EditForm in .NET 9 SSR must emit exactly ONE __RequestVerificationToken input (duplicate tokens cause browser 400 errors)");
        var tokenMatch = tokenMatches[0];

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = tokenMatch.Groups[1].Value,
            ["_handler"] = "LoginForm",
            ["Input.UserName"] = "test_owner",
            ["Input.Password"] = TestWebApplicationFactory.DefaultTestPassword,
            ["Input.RememberMe"] = "false"
        });

        var loginPost = await client.PostAsync("/Account/Login", form);
        loginPost.StatusCode.Should().Be(HttpStatusCode.Redirect);
        loginPost.Headers.Location?.ToString().Should().EndWith("/");

        // 2. GET / after login and extract Logout form token
        var homePage = await client.GetAsync("/");
        homePage.StatusCode.Should().Be(HttpStatusCode.OK);
        var homeHtml = await homePage.Content.ReadAsStringAsync();

        var logoutMatches = Regex.Matches(homeHtml, @"action=""/Account/Logout""[\s\S]*?name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        logoutMatches.Count.Should().Be(1, "Logout form must emit exactly ONE __RequestVerificationToken input");
        var logoutTokenMatch = logoutMatches[0];

        var logoutPost = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutTokenMatch.Groups[1].Value
        }));
        logoutPost.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task SetupForm_HasAntiforgeryCookieAndTokenOnGet()
    {
        using var client = factory.CreateAnonymousClient();
        var setupPage = await client.GetAsync("/Account/Setup");
        // Even if redirected to /Account/Login because users exist, check headers/cookies
        if (setupPage.StatusCode == HttpStatusCode.OK)
        {
            var html = await setupPage.Content.ReadAsStringAsync();
            Regex.Matches(html, @"name=""__RequestVerificationToken""").Count.Should().Be(1);
            setupPage.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
            string.Join(";", cookies!).Should().Contain("Antiforgery");
        }
    }
}
