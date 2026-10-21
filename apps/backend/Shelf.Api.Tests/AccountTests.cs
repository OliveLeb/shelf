using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class AccountTests : TestInstance
{
    private static Task<HttpResponseMessage> DeleteSelf(HttpClient client, string password) => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/account") { Content = JsonContent.Create(new { currentPassword = password }) });

    [Fact]
    public async Task Username_change_preserves_identity_and_rejects_conflicts()
    {
        var (id, member) = await User();
        var response = await member.PatchAsJsonAsync("/account/username", new { username = "renamed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, (await Json(response)).GetProperty("id").GetGuid());
        using var renamed = await SignIn("RENAMED");
        Assert.Equal(HttpStatusCode.Conflict, (await member.PatchAsJsonAsync("/account/username", new { username = "OWNER" })).StatusCode);
        Assert.Equal("renamed", (await Json(await member.GetAsync("/account"))).GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/auth/login", new { identifier = "member", password = Password })).StatusCode);
    }
    [Theory]
    [InlineData("email")]
    [InlineData("password")]
    public async Task Sensitive_change_requires_password_and_revokes_other_sessions_only_after_success(string field)
    {
        var (_, member) = await User();
        using var phone = await SignIn("member");
        object Invalid() => field == "email" ? new { email = "new@example.test", currentPassword = "wrong" } : new { password = "another-long-test-password", currentPassword = "wrong" };
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PatchAsJsonAsync("/account/" + field, Invalid())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/account")).StatusCode);
        object Valid() => field == "email" ? new { email = "new@example.test", currentPassword = Password } : new { password = "another-long-test-password", currentPassword = Password };
        Assert.True((await member.PatchAsJsonAsync("/account/" + field, Valid())).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/account")).StatusCode);
        if (field == "email")
        {
            using var changed = await SignIn("new@example.test");
            using var sameUsername = await SignIn("member");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/auth/login", new { identifier = "member@example.test", password = Password })).StatusCode);
        }
        else
        {
            using var changed = await SignIn("member", "another-long-test-password");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/auth/login", new { identifier = "member", password = Password })).StatusCode);
        }
    }
    [Fact]
    public async Task Duplicate_email_and_invalid_password_changes_are_atomic()
    {
        var (_, member) = await User();
        using var phone = await SignIn("member");
        Assert.Equal(HttpStatusCode.Conflict, (await member.PatchAsJsonAsync("/account/email", new { email = "OWNER@EXAMPLE.TEST", currentPassword = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PatchAsJsonAsync("/account/password", new { password = "short", currentPassword = Password })).StatusCode);
        Assert.Equal("member@example.test", (await Json(await member.GetAsync("/account"))).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/account")).StatusCode);
    }
    [Fact]
    public async Task Self_deletion_requires_password_and_invalidates_all_access_and_recovery()
    {
        var (id, member) = await User();
        using var phone = await SignIn("member");
        var recovery = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteSelf(member, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteSelf(member, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/auth/reset-password", new { token = Token(recovery), password = Password })).StatusCode);
        Assert.DoesNotContain((await Json(await Admin.GetAsync("/admin/accounts"))).EnumerateArray(), a => a.GetProperty("id").GetGuid() == id);
        // Identifiers can be reused after permanent deletion.
        await User();
    }
    [Fact]
    public async Task Disabled_account_keeps_its_identifiers_but_loses_login_sessions_and_reset_access()
    {
        var (id, member) = await User();
        var recovery = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/admin/accounts/{id}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
        var accounts = await Json(await Admin.GetAsync("/admin/accounts"));
        Assert.False(accounts.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/auth/login", new { identifier = "member", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync("/admin/invitations", new { email = "MEMBER@example.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/auth/reset-password", new { token = Token(recovery), password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null)).StatusCode);
    }
    [Fact]
    public async Task Ordinary_users_cannot_change_other_accounts_or_administer_the_instance()
    {
        var (_, member) = await User();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/admin/accounts/{AdminId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/admin/accounts/{AdminId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/admin/accounts/{AdminId}/password-reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/admin/invitations", new { email = "another@example.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PatchAsJsonAsync($"/accounts/{AdminId}", new { username = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsync($"/admin/accounts/{AdminId}/grant", null)).StatusCode);
    }
    [Fact]
    public async Task Deletion_removes_consumed_invitation_metadata_even_after_email_change()
    {
        var (id, member) = await User();
        Assert.True((await member.PatchAsJsonAsync("/account/email", new { email = "new@example.test", currentPassword = Password })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{id}")).StatusCode);
        var invitations = await Json(await Admin.GetAsync("/admin/invitations"));
        Assert.Empty(invitations.EnumerateArray());
    }
    [Fact]
    public async Task Administrative_deletion_removes_account_without_its_password()
    {
        var (id, member) = await User();
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/admin/accounts/{id}")).StatusCode);
    }
}
