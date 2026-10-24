using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class WebSessionTests : TestInstance
{
    private const string CookieName = "__Host-shelf-session";
    private const string Origin = "https://shelf.example.test";

    private HttpClient Browser(string? cookie = null)
    {
        var client = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(Origin) });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("Origin", Origin);
        client.DefaultRequestHeaders.Add("X-Shelf-CSRF", "1");
        return client;
    }

    private static Task<HttpResponseMessage> Login(HttpClient browser, string identifier = "owner", string password = Password) =>
        browser.PostAsJsonAsync("/auth/web/login", new { identifier, password, deviceName = "Web browser" });

    private static SetCookieHeaderValue Cookie(HttpResponseMessage response) =>
        Assert.Single(SetCookieHeaderValue.ParseList(response.Headers.GetValues("Set-Cookie").ToList()), c => c.Name == CookieName);

    private static string Credential(HttpResponseMessage response) => $"{CookieName}={Cookie(response).Value}";

    [Fact]
    public async Task Web_login_returns_only_account_and_a_persistent_host_cookie()
    {
        using var browser = Browser();
        var response = await Login(browser);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = await Json(response);
        Assert.Equal(AdminId, account.GetProperty("id").GetGuid());
        Assert.Equal(new[] { "email", "id", "isActive", "isAdmin", "username" }, account.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var cookie = Cookie(response);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/", cookie.Path.ToString());
        Assert.False(cookie.Domain.HasValue);
        Assert.InRange(cookie.Expires!.Value, Clock.GetUtcNow().AddDays(30).AddSeconds(-1), Clock.GetUtcNow().AddDays(30));
        Assert.Equal(64, cookie.Value.Length);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(cookie.Value.ToString(), string.Join('\n', Logs.Messages));
    }

    [Fact]
    public async Task Configured_origin_with_trailing_slash_accepts_browser_origin()
    {
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Authentication:ClientOrigin"] = Origin + "/" })));
        using var browser = Browser();
        var login = await Login(browser);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var authenticated = Browser(Credential(login));
        Assert.Equal(HttpStatusCode.NoContent, (await authenticated.PostAsync("/auth/logout", null)).StatusCode);
    }

    [Theory]
    [InlineData("/account")]
    [InlineData("/spaces/personal/elements")]
    public async Task Cookie_activity_slides_expiry_and_logout_revokes_only_that_session(string route)
    {
        using var loginClient = Browser();
        var login = await Login(loginClient);
        using var browser = Browser(Credential(login));
        Clock.Advance(TimeSpan.FromDays(29));
        var reading = await browser.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, reading.StatusCode);
        Assert.Equal(Cookie(login).Value, Cookie(reading).Value);
        Assert.InRange(Cookie(reading).Expires!.Value, Clock.GetUtcNow().AddDays(30).AddSeconds(-1), Clock.GetUtcNow().AddDays(30));
        var sessions = await Json(await browser.GetAsync("/account/sessions"));
        var expiry = sessions.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiry - Cookie(reading).Expires!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(route)).StatusCode);
        // The control Bearer session also needs activity within its own window.
        using var mobile = await SignIn("owner");
        var logout = await browser.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(Cookie(logout).Expires < DateTimeOffset.UtcNow);
        var rejected = await browser.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.True(Cookie(rejected).Expires < DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("null", "1")]
    [InlineData("https://foreign.example.test", "1")]
    [InlineData("https://shelf.example.test.evil.test", "1")]
    [InlineData("https://shelf.example.test/", "1")]
    [InlineData(Origin, null)]
    [InlineData(Origin, "0")]
    [InlineData(Origin, "1, 1")]
    public async Task Every_web_unsafe_operation_requires_exact_origin_and_csrf_header(string? origin, string? csrf)
    {
        using var loginClient = Browser();
        var login = await Login(loginClient);
        using var browser = Browser(Credential(login));
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.Remove("X-Shelf-CSRF");
        if (origin is not null) browser.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
        if (csrf is not null) browser.DefaultRequestHeaders.TryAddWithoutValidation("X-Shelf-CSRF", csrf);
        // Login must be protected even before the browser has a cookie.
        using var anonymous = Browser();
        anonymous.DefaultRequestHeaders.Remove("Origin");
        anonymous.DefaultRequestHeaders.Remove("X-Shelf-CSRF");
        if (origin is not null) anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
        if (csrf is not null) anonymous.DefaultRequestHeaders.TryAddWithoutValidation("X-Shelf-CSRF", csrf);
        var responses = new[]
        {
            await Login(anonymous),
            await Login(browser),
            await browser.PostAsync("/auth/logout", null),
            await browser.PostAsJsonAsync("/folders", new { name = "forbidden" }),
            await browser.PatchAsJsonAsync("/account/username", new { username = "forbidden" })
        };
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        Assert.Equal("owner", (await Json(await browser.GetAsync("/account"))).GetProperty("username").GetString());
        Assert.Empty(await Root(browser));
    }

    [Theory]
    [InlineData("/auth/web/login/")]
    [InlineData("/AUTH/WEB/LOGIN")]
    public async Task Login_route_aliases_cannot_bypass_csrf(string route)
    {
        var response = await Client.PostAsJsonAsync(route, new { identifier = "owner", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Web_relogin_replaces_previous_session_even_when_switching_accounts_and_legacy_login_cannot_leak_tokens()
    {
        var (memberId, member) = await User();
        using var loginClient = Browser();
        var first = await Login(loginClient);
        using var oldBrowser = Browser(Credential(first));
        var legacy = await oldBrowser.PostAsJsonAsync("/auth/login", new { identifier = "owner", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        Assert.DoesNotContain("token", await legacy.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.False(legacy.Headers.Contains("Set-Cookie"));
        var replacement = await Login(oldBrowser);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Assert.NotEqual(Credential(first), Credential(replacement));
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldBrowser.GetAsync("/account")).StatusCode);
        using var sameAccount = Browser(Credential(replacement));
        var sessions = await Json(await sameAccount.GetAsync("/account/sessions"));
        Assert.Equal(2, sessions.GetArrayLength()); // existing Admin Bearer + one web session
        var switched = await Login(sameAccount, "member");
        Assert.Equal(memberId, (await Json(switched)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await sameAccount.GetAsync("/account")).StatusCode);
        using var newBrowser = Browser(Credential(switched));
        Assert.Equal(memberId, (await Json(await newBrowser.GetAsync("/account"))).GetProperty("id").GetGuid());
        Assert.Single((await Json(await Admin.GetAsync("/account/sessions"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/account")).StatusCode);
    }

    [Theory]
    [InlineData("valid", "valid", true)]
    [InlineData("invalid", "valid", true)]
    [InlineData("", "valid", true)]
    [InlineData("valid", "invalid", true)]
    [InlineData("valid", "valid", false)]
    public async Task Cookie_and_authorization_are_always_rejected_without_fallback(string cookieKind, string authorizationKind, bool csrfHeaders)
    {
        using var loginClient = Browser();
        var login = await Login(loginClient);
        using var browser = Browser(cookieKind == "valid" ? Credential(login) : $"{CookieName}={cookieKind}");
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorizationKind == "valid" ? Admin.DefaultRequestHeaders.Authorization!.ToString() : authorizationKind);
        if (!csrfHeaders)
        {
            browser.DefaultRequestHeaders.Remove("Origin");
            browser.DefaultRequestHeaders.Remove("X-Shelf-CSRF");
        }
        foreach (var response in new[]
        {
            await browser.GetAsync("/account"),
            await browser.GetAsync("/spaces/personal/elements"),
            await browser.PostAsJsonAsync("/folders", new { name = "forbidden" }),
            await browser.PostAsync("/auth/logout", null),
            await Login(browser),
            await browser.PostAsJsonAsync("/auth/login", new { identifier = "owner", password = Password })
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("ambiguous_credentials", await Error(response));
            Assert.True(Cookie(response).Expires < DateTimeOffset.UtcNow);
        }
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync("/account")).StatusCode);
        using var unmixed = Browser(Credential(login));
        Assert.Equal(HttpStatusCode.OK, (await unmixed.GetAsync("/account")).StatusCode);
        Assert.Empty(await Root(unmixed));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("revoke")]
    [InlineData("disable")]
    public async Task Expired_revoked_or_disabled_cookie_is_cleared_on_auth_and_storage(string withdrawal)
    {
        var (id, mobile) = await User();
        using var loginClient = Browser();
        var login = await Login(loginClient, "member");
        using var browser = Browser(Credential(login));
        if (withdrawal == "expiry") Clock.Advance(TimeSpan.FromDays(30));
        else if (withdrawal == "disable") Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/admin/accounts/{id}/disable", null)).StatusCode);
        else
        {
            var sessions = await Json(await browser.GetAsync("/account/sessions"));
            var sessionId = sessions.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await mobile.DeleteAsync($"/account/sessions/{sessionId}")).StatusCode);
        }
        foreach (var route in new[] { "/account", "/spaces/personal/elements" })
        {
            var rejected = await browser.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var cookie = Cookie(rejected);
            Assert.True(cookie.Expires < DateTimeOffset.UtcNow);
            Assert.True(cookie.HttpOnly && cookie.Secure);
            Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
            Assert.Equal("/", cookie.Path.ToString());
            Assert.False(cookie.Domain.HasValue);
        }
        if (withdrawal != "disable") Assert.Equal(HttpStatusCode.OK, (await Login(browser, "member")).StatusCode);
    }

    [Fact]
    public async Task Cookie_survives_api_restart_and_personal_roots_remain_private_even_from_administrators()
    {
        var (memberId, member) = await User();
        var (_, other) = await User("other");
        await Upload(other, "other-private.jpg");
        using var loginClient = Browser();
        var login = await Login(loginClient, "member");
        using var beforeRestart = Browser(Credential(login));
        var file = await Upload(beforeRestart, "member-private.jpg");
        Assert.Equal(memberId, file.GetProperty("addedBy").GetProperty("id").GetGuid());
        Assert.Equal("member-private.jpg", Assert.Single(await Root(member)).GetProperty("name").GetString());
        await RestartApp();
        using var browser = Browser(Credential(login));
        Assert.Equal(memberId, (await Json(await browser.GetAsync("/account"))).GetProperty("id").GetGuid());
        Assert.Equal("member-private.jpg", Assert.Single(await Root(browser)).GetProperty("name").GetString());
        using var adminLoginClient = Browser();
        using var webAdmin = Browser(Credential(await Login(adminLoginClient)));
        using var otherAfterRestart = await SignIn("other");
        Assert.Empty(await Root(webAdmin));
        foreach (var caller in new[] { webAdmin, Admin, otherAfterRestart })
        {
            var denied = await caller.GetAsync($"/files/{Id(file)}/download");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/files/{Id(file)}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await webAdmin.PostAsJsonAsync("/folders", new { name = "admin-personal" })).StatusCode);
        Assert.Equal("admin-personal", Assert.Single(await Root(webAdmin)).GetProperty("name").GetString());
        Assert.Equal("member-private.jpg", Assert.Single(await Root(browser)).GetProperty("name").GetString());
        var forbiddenAdmin = await browser.GetAsync("/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdmin.StatusCode);
        Assert.False(forbiddenAdmin.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-session")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_cookie_alone_is_cleared_on_account_and_storage(string value)
    {
        using var browser = Browser($"{CookieName}={value}");
        foreach (var route in new[] { "/account", "/spaces/personal/elements" })
        {
            var response = await browser.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("authentication_required", await Error(response));
            Assert.True(Cookie(response).Expires < DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task Failed_authenticated_operations_do_not_renew_cookies_and_failed_login_does_not_create_or_replace_sessions()
    {
        var (_, member) = await User();
        using var loginClient = Browser();
        var login = await Login(loginClient, "member");
        using var browser = Browser(Credential(login));
        var before = (await Json(await member.GetAsync("/account/sessions"))).GetArrayLength();
        Clock.Advance(TimeSpan.FromDays(1));
        var invalidUsername = await browser.PatchAsJsonAsync("/account/username", new { username = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUsername.StatusCode);
        Assert.False(invalidUsername.Headers.Contains("Set-Cookie"));
        var invalidFolder = await browser.PostAsJsonAsync("/folders", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidFolder.StatusCode);
        Assert.False(invalidFolder.Headers.Contains("Set-Cookie"));
        var failedLogin = await Login(browser, "member", "incorrect-password");
        Assert.Equal(HttpStatusCode.Unauthorized, failedLogin.StatusCode);
        Assert.True(Cookie(failedLogin).Expires < DateTimeOffset.UtcNow);
        Assert.Equal(before, (await Json(await member.GetAsync("/account/sessions"))).GetArrayLength());
        // HTTP callers replaying the old secret can still use it: a failed switch did not revoke it.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account")).StatusCode);
    }

    [Fact]
    public async Task Csrf_is_checked_before_json_binding_and_multiple_origin_values_are_rejected()
    {
        using var browser = Browser();
        browser.DefaultRequestHeaders.Remove("Origin");
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Origin", new[] { Origin, "https://foreign.example.test" });
        var response = await browser.PostAsync("/auth/web/login", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_rejected", await Error(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
