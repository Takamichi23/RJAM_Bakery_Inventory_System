using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using RjamBakery.Application.Contracts;
using RjamBakery.Domain.Common;
using RjamBakery.Infrastructure.Reporting;
using RjamBakery.Web.Security;

namespace RjamBakery.IntegrationTests;

[Collection("BakeryIntegration")]
public sealed class SecurityHardeningTests(TestWebApplicationFactory factory)
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/pos", "/pos")]
    [InlineData("/products?search=bread", "/products?search=bread")]
    [InlineData("//evil.example/phish", "/")]
    [InlineData(@"/\evil.example", "/")]
    [InlineData("https://evil.example/phish", "/")]
    [InlineData("http://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/%2fevil.example", "/")]
    [InlineData("/%5cevil.example", "/")]
    [InlineData("/pos\r\nLocation: https://evil.example", "/")]
    [InlineData("/Account/Login", "/")]
    [InlineData("/Account/Logout", "/")]
    public void SecurityUrlHelper_PreventsOpenRedirects(string? input, string expected)
    {
        SecurityUrlHelper.GetSafeLocalReturnUrl(input).Should().Be(expected);
    }

    [Fact]
    public void ExcelExport_SanitizesSpreadsheetFormulaInjectionPayloads()
    {
        var exporter = new ReportExportService();
        var report = new InventoryReportDto(
            GeneratedAtUtc: DateTime.UtcNow,
            RawMaterials: [],
            FactorySummary: [],
            FactoryLots: [],
            StoreInventory:
            [
                new StoreInventoryDto(
                    1, 1, "=CMD|' /C calc'!A0", "+HYPERLINK(\"https://evil.example\")",
                    "Breads", "pcs", 25m, 10m, 10m, 5m, "InStock", null, null, DateTime.UtcNow)
            ],
            LowAndOutOfStockItems: []);

        var file = exporter.ExportInventoryExcel(report);

        using var ms = new MemoryStream(file.Content);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("Store Inventory");

        ws.Cell(2, 1).HasFormula.Should().BeFalse();
        ws.Cell(2, 1).DataType.Should().Be(XLDataType.Text);
        ws.Cell(2, 1).Style.IncludeQuotePrefix.Should().BeTrue();
        ws.Cell(2, 2).HasFormula.Should().BeFalse();
        ws.Cell(2, 2).DataType.Should().Be(XLDataType.Text);
        ws.Cell(2, 2).Style.IncludeQuotePrefix.Should().BeTrue();
        ws.Cell(2, 4).GetDouble().Should().Be(10d);
    }

    [Fact]
    public async Task SecurityHeadersAndCookieFlags_AreEnforcedAcrossWebAndApi()
    {
        using var anon = factory.CreateAnonymousClient();
        var loginPage = await anon.GetAsync("/Account/Login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertCoreSecurityHeaders(loginPage);
        loginPage.Headers.CacheControl?.NoStore.Should().BeTrue();
        loginPage.Headers.TryGetValues("Set-Cookie", out var afCookies).Should().BeTrue();
        string.Join(";", afCookies!).Should().Contain("Rjam.Antiforgery").And.ContainEquivalentOf("httponly");

        var loginResp = await anon.PostAsJsonAsync("/api/auth/login", new
        {
            UserName = "test_owner",
            Password = TestWebApplicationFactory.DefaultTestPassword
        });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertCoreSecurityHeaders(loginResp);
        loginResp.Headers.CacheControl?.NoStore.Should().BeTrue();
        loginResp.Headers.TryGetValues("Set-Cookie", out var authCookies).Should().BeTrue();
        var authCookieStr = string.Join(";", authCookies!);
        authCookieStr.Should().Contain("Rjam.Auth");
        authCookieStr.Should().ContainEquivalentOf("httponly");
        authCookieStr.Should().ContainEquivalentOf("samesite=lax");

        var apiResp = await anon.GetAsync("/api/categories");
        apiResp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertCoreSecurityHeaders(apiResp);
        apiResp.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task ApiCsrfAndOriginProtection_BlocksCrossOriginAndFormEncodedMutations()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        // 1. Cross-origin Origin header must be blocked (403 CSRF_ORIGIN_MISMATCH)
        using var badOriginReq = new HttpRequestMessage(HttpMethod.Post, "/api/categories")
        {
            Content = JsonContent.Create(new SaveCategoryRequest(null, "EvilOriginCat", "", 99))
        };
        badOriginReq.Headers.Add("Origin", "https://evil.example");
        var badOriginResp = await owner.SendAsync(badOriginReq);
        badOriginResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await badOriginResp.Content.ReadAsStringAsync()).Should().Contain("CSRF_ORIGIN_MISMATCH");

        // 2. Sec-Fetch-Site: cross-site must be blocked (403 CSRF_BLOCKED)
        using var crossSiteReq = new HttpRequestMessage(HttpMethod.Post, "/api/categories")
        {
            Content = JsonContent.Create(new SaveCategoryRequest(null, "CrossSiteCat", "", 99))
        };
        crossSiteReq.Headers.Add("Sec-Fetch-Site", "cross-site");
        var crossSiteResp = await owner.SendAsync(crossSiteReq);
        crossSiteResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await crossSiteResp.Content.ReadAsStringAsync()).Should().Contain("CSRF_BLOCKED");

        // 3. Classic HTML form POST content-type on /api/* must be blocked (415)
        using var formPostReq = new HttpRequestMessage(HttpMethod.Post, "/api/categories")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "FormCsrf" })
        };
        var formPostResp = await owner.SendAsync(formPostReq);
        formPostResp.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await formPostResp.Content.ReadAsStringAsync()).Should().Contain("UNSUPPORTED_MEDIA_TYPE");

        // 4. Matching same-origin request succeeds cleanly
        using var sameOriginReq = new HttpRequestMessage(HttpMethod.Post, "/api/categories")
        {
            Content = JsonContent.Create(new SaveCategoryRequest(null, $"SafeCat-{Guid.NewGuid():N}"[..20], "Safe", 88))
        };
        sameOriginReq.Headers.Add("Origin", "http://localhost");
        sameOriginReq.Headers.Add("Sec-Fetch-Site", "same-origin");
        var sameOriginResp = await owner.SendAsync(sameOriginReq);
        sameOriginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebLogin_SupportsEmailSignIn_RendersPasswordToggle_AndNeutralizesOpenRedirectReturnUrl()
    {
        using var client = factory.CreateAnonymousClient();
        var loginPage = await client.GetAsync("/Account/Login?ReturnUrl=//evil.example/steal");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginPage.Content.ReadAsStringAsync();
        html.Should().Contain("Username or Email");
        html.Should().Contain("data-toggle-password=\"password\"");
        var token = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

        // Sign in using the Baker's EMAIL address instead of username
        var post = await client.PostAsync("/Account/Login?ReturnUrl=//evil.example/steal",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["_handler"] = "LoginForm",
                ["Input.UserName"] = "baker@rjam.test",
                ["Input.Password"] = TestWebApplicationFactory.DefaultTestPassword,
                ["Input.RememberMe"] = "false"
            }));

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = post.Headers.Location?.ToString() ?? string.Empty;
        location.Should().NotContain("evil.example");
        location.Should().EndWith("/");

        // API login also accepts email address
        using var apiClient = factory.CreateAnonymousClient();
        var apiLogin = await apiClient.PostAsJsonAsync("/api/auth/login", new
        {
            UserName = "owner@rjam.test",
            Password = TestWebApplicationFactory.DefaultTestPassword
        });
        apiLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePasswordWorkflow_AndFailedLoginAuditing_WorkEndToEnd()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        var uname = $"sec_user_{Guid.NewGuid():N}"[..16];
        var createResp = await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(
            uname, "Security Test Staff", $"{uname}@rjam.test", AppRoles.Cashier, "InitPass#2026", MustChangePassword: true));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Anonymous request to /Account/ChangePassword must redirect to Login
        using var anon = factory.CreateAnonymousClient();
        var anonChangePage = await anon.GetAsync("/Account/ChangePassword");
        anonChangePage.StatusCode.Should().Be(HttpStatusCode.Redirect);
        anonChangePage.Headers.Location?.ToString().Should().Contain("/Account/Login");

        // Failed login must return 401 and record UserLoginFailed audit log
        var badLogin = await anon.PostAsJsonAsync("/api/auth/login", new { UserName = uname, Password = "WrongPassword#1" });
        badLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var auditLogs = await owner.GetFromJsonAsync<PagedResult<AuditLogDto>>("/api/audit-logs?action=UserLoginFailed&pageSize=20");
        auditLogs!.Items.Should().Contain(a => a.UserName == uname && a.Action == "UserLoginFailed");

        // Valid login + change password via API
        using var staffClient = await factory.CreateAuthenticatedClientAsync(uname, "InitPass#2026");

        // Authenticated /Account/ChangePassword page renders form cleanly
        var changePage = await staffClient.GetAsync("/Account/ChangePassword");
        changePage.StatusCode.Should().Be(HttpStatusCode.OK);
        var changeHtml = await changePage.Content.ReadAsStringAsync();
        Regex.Matches(changeHtml, @"name=""__RequestVerificationToken""").Count.Should().Be(1);

        // Reusing same password is rejected
        var samePw = await staffClient.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = "InitPass#2026",
            NewPassword = "InitPass#2026"
        });
        samePw.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Strong new password succeeds
        var changeOk = await staffClient.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = "InitPass#2026",
            NewPassword = "RotatedPass#2027"
        });
        changeOk.StatusCode.Should().Be(HttpStatusCode.OK);

        // Old password fails, new password succeeds and clears MustChangePassword
        using var oldCheck = factory.CreateAnonymousClient();
        (await oldCheck.PostAsJsonAsync("/api/auth/login", new { UserName = uname, Password = "InitPass#2026" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var newCheck = factory.CreateAnonymousClient();
        var newLoginResp = await newCheck.PostAsJsonAsync("/api/auth/login", new { UserName = uname, Password = "RotatedPass#2027" });
        newLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await newLoginResp.Content.ReadAsStringAsync()).Should().Contain("\"mustChangePassword\":false");
    }

    [Fact]
    public async Task MalformedApiPayload_InvertedReportRange_AndErrorPage_AreSanitized()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync("test_owner");

        // Malformed JSON -> 400 ProblemDetails, no stack trace
        using var badJsonReq = new HttpRequestMessage(HttpMethod.Post, "/api/categories")
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/json")
        };
        var badJsonResp = await owner.SendAsync(badJsonReq);
        badJsonResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var badJsonBody = await badJsonResp.Content.ReadAsStringAsync();
        badJsonBody.Should().Contain("INVALID_REQUEST");
        badJsonBody.Should().NotContain("System.Text.Json");

        // Inverted report date range -> 400
        var badReport = await owner.GetAsync("/api/reports/production?fromDate=2026-12-31&toDate=2026-01-01");
        badReport.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // /Error page must not disclose ASPNETCORE_ENVIRONMENT or Development Mode text
        using var anon = factory.CreateAnonymousClient();
        var errResp = await anon.GetAsync("/Error");
        errResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var errHtml = await errResp.Content.ReadAsStringAsync();
        errHtml.Should().Contain("Something Went Wrong");
        errHtml.Should().NotContain("ASPNETCORE_ENVIRONMENT");
        errHtml.Should().NotContain("Development Mode");
    }

    [Fact]
    public async Task AuthRateLimiter_BlocksExcessiveRapidLoginAttemptsWith429()
    {
        using var limitedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:EnableRateLimiting", "true");
            builder.UseSetting("Security:AuthPermitLimitPerMinute", "3");
        });

        using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        HttpResponseMessage? lastResp = null;
        for (int i = 0; i < 4; i++)
        {
            lastResp = await client.PostAsJsonAsync("/api/auth/login", new
            {
                UserName = "nonexistent_rate_limit_probe",
                Password = "WrongPassword#1"
            });

            if (i < 3)
            {
                lastResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
        }

        lastResp!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        lastResp.Headers.TryGetValues("Retry-After", out _).Should().BeTrue();
        (await lastResp.Content.ReadAsStringAsync()).Should().Contain("RATE_LIMIT_EXCEEDED");
    }

    private static void AssertCoreSecurityHeaders(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).Should().BeTrue();
        nosniff.Should().Contain("nosniff");

        response.Headers.TryGetValues("X-Frame-Options", out var frameOpts).Should().BeTrue();
        frameOpts.Should().Contain("DENY");

        response.Headers.TryGetValues("Referrer-Policy", out var refPolicy).Should().BeTrue();
        refPolicy.Should().Contain("strict-origin-when-cross-origin");

        response.Headers.TryGetValues("Permissions-Policy", out _).Should().BeTrue();
        response.Headers.TryGetValues("Cross-Origin-Opener-Policy", out _).Should().BeTrue();
        response.Headers.TryGetValues("Cross-Origin-Resource-Policy", out _).Should().BeTrue();

        response.Headers.TryGetValues("Content-Security-Policy", out var csp).Should().BeTrue();
        var cspVal = string.Join(";", csp!);
        cspVal.Should().Contain("frame-ancestors 'none'");
        cspVal.Should().Contain("object-src 'none'");

        response.Headers.Contains("X-Powered-By").Should().BeFalse();
    }
}
