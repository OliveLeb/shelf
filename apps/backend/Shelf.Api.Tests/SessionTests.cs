using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SessionTests : TestInstance
{
    [Fact]
    public async Task Login_supports_username_and_email_and_hides_unknown_identifiers()
    {
        using var username = await SignIn("OWNER");
        using var email = await SignIn("OWNER@EXAMPLE.TEST");
        var wrong = await Client.PostAsJsonAsync("/auth/login", new { identifier = "owner", password = "wrong" });
        var unknown = await Client.PostAsJsonAsync("/auth/login", new { identifier = "unknown", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/admin/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health")).StatusCode);
    }
    [Fact]
    public async Task Devices_have_private_sessions_with_current_marker_and_targeted_revocation()
    {
        var (_, member) = await User();
        using var otherDevice = await SignIn("member", device: "phone");
        var sessions = await Json(await member.GetAsync("/account/sessions"));
        Assert.Equal(2, sessions.GetArrayLength());
        Assert.Single(sessions.EnumerateArray(), s => s.GetProperty("isCurrent").GetBoolean());
        var other = sessions.EnumerateArray().Single(s => !s.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("phone", other.GetProperty("deviceName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync("/account/sessions/" + other.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync("/account/sessions/" + other.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
    }
    [Fact]
    public async Task Revoking_all_sessions_includes_the_current_device()
    {
        var (_, member) = await User();
        using var phone = await SignIn("member");
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync("/account/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/account")).StatusCode);
    }
    [Fact]
    public async Task Activity_renews_thirty_day_window_but_expired_sessions_never_revive()
    {
        var (_, active) = await User();
        using var idle = await SignIn("member");
        Clock.Advance(TimeSpan.FromDays(29));
        var list = await Json(await active.GetAsync("/account/sessions"));
        var current = list.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean());
        Assert.InRange(Clock.GetUtcNow().AddDays(30) - current.GetProperty("expiresAt").GetDateTimeOffset(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/account")).StatusCode);
        Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await active.GetAsync("/account")).StatusCode);
    }
    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/register")]
    [InlineData("/auth/reset-password")]
    public async Task Public_secret_consumption_is_rate_limited(string route)
    {
        await using var limited = App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:PublicPermitLimit"] = "2" })));
        using var client = limited.CreateClient();
        await client.PostAsJsonAsync(route, new { });
        await client.PostAsJsonAsync(route, new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(route, new { })).StatusCode);
    }
}
