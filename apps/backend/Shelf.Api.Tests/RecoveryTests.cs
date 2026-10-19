using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class RecoveryTests : TestInstance
{
    [Fact]
    public async Task Reset_replaces_previous_links_and_revokes_every_session()
    {
        var (id, member) = await User();
        using var phone = await SignIn("member");
        var old = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        var replacement = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(Token(old))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(Token(replacement))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(Token(replacement))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/account")).StatusCode);
        using var recovered = await SignIn("member", "changed-recovery-password");
    }
    private Task<HttpResponseMessage> Reset(string token, string password = "changed-recovery-password") => Client.PostAsJsonAsync("/auth/reset-password", new { token, password });

    [Fact]
    public async Task Reset_expires_at_one_hour_and_rejects_forgery_and_weak_password_without_consumption()
    {
        var (id, _) = await User();
        var link = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(Token(link), "short")).StatusCode);
        Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(Token(link))).StatusCode);
        var expiring = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(Token(expiring))).StatusCode);
    }
    [Fact]
    public async Task Password_change_invalidates_pending_reset_links()
    {
        var (id, member) = await User();
        var link = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Assert.Equal(HttpStatusCode.NoContent, (await member.PatchAsJsonAsync("/account/password", new { password = "new-current-password", currentPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(Token(link))).StatusCode);
    }
    [Fact]
    public async Task Concurrent_reset_consumption_succeeds_once()
    {
        var (id, _) = await User();
        var link = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        var results = await Task.WhenAll(Reset(Token(link)), Reset(Token(link)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
        using var recovered = await SignIn("member", "changed-recovery-password");
    }
}
