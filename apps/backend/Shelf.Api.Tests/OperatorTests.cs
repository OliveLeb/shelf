using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class OperatorTests : TestInstance
{
    [Fact]
    public async Task Commands_create_additional_admins_and_grant_and_revoke_roles_visible_to_existing_sessions()
    {
        var created = await Cli(Password, "admin", "create", "second", "second@example.test");
        Assert.Equal(0, created.Code);
        using var second = await SignIn("second");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/admin/accounts")).StatusCode);
        var (id, member) = await User();
        Assert.Equal(0, (await Cli(null, "admin", "grant", id.ToString())).Code);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/admin/accounts")).StatusCode);
        Assert.Equal(0, (await Cli(null, "admin", "revoke", id.ToString())).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/admin/accounts")).StatusCode);
        var snapshot = await Json(await Admin.GetAsync("/admin/accounts"));
        var account = snapshot.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);
        Assert.Equal("member", account.GetProperty("username").GetString());
        Assert.False(account.GetProperty("isAdmin").GetBoolean());
    }
    [Fact]
    public async Task Admin_password_recovery_enforces_policy_and_revokes_sessions_and_links()
    {
        var link = await Json(await Admin.PostAsync($"/admin/accounts/{AdminId}/password-reset", null));
        Assert.Equal(2, (await Cli("short", "admin", "reset-password", AdminId.ToString())).Code);
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync("/account")).StatusCode);
        const string newPassword = "recovered-operator-password";
        var recovery = await Cli(newPassword, "admin", "reset-password", AdminId.ToString());
        Assert.Equal(0, recovery.Code);
        Assert.DoesNotContain(newPassword, recovery.Output + recovery.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/auth/reset-password", new { token = Token(link), password = Password })).StatusCode);
        using var recovered = await SignIn("owner", newPassword);
    }
    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("self-delete")]
    [InlineData("revoke")]
    public async Task Last_active_administrator_is_protected_at_every_entry(string operation)
    {
        var status = operation switch
        {
            "disable" => (await Admin.PostAsync($"/admin/accounts/{AdminId}/disable", null)).StatusCode,
            "delete" => (await Admin.DeleteAsync($"/admin/accounts/{AdminId}")).StatusCode,
            "self-delete" => (await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/account") { Content = JsonContent.Create(new { currentPassword = Password }) })).StatusCode,
            _ => (await Cli(null, "admin", "revoke", AdminId.ToString())).Code == 4 ? HttpStatusCode.Conflict : HttpStatusCode.OK
        };
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync("/admin/accounts")).StatusCode);
    }
    [Fact]
    public async Task Disabled_admin_does_not_count_as_an_available_administrative_access()
    {
        var created = await Cli(Password, "admin", "create", "second", "second@example.test");
        var secondId = Guid.Parse(created.Output.Trim());
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/admin/accounts/{secondId}/disable", null)).StatusCode);
        Assert.Equal(4, (await Cli(null, "admin", "revoke", AdminId.ToString())).Code);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.DeleteAsync($"/admin/accounts/{AdminId}")).StatusCode);
        Assert.Equal(0, (await Cli(Password, "admin", "reset-password", secondId.ToString())).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/auth/login", new { identifier = "second", password = Password })).StatusCode);
    }
    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("revoke")]
    public async Task Concurrent_removal_of_the_last_two_admins_preserves_one_access(string operation)
    {
        var created = await Cli(Password, "admin", "create", "second", "second@example.test");
        var id = Guid.Parse(created.Output.Trim());
        using var second = await SignIn("second");
        if (operation == "revoke")
        {
            var results = await Task.WhenAll(Cli(null, "admin", "revoke", AdminId.ToString()), Cli(null, "admin", "revoke", id.ToString()));
            Assert.Single(results, r => r.Code == 0);
            Assert.Single(results, r => r.Code == 4);
        }
        else
        {
            Task<HttpResponseMessage> Remove(HttpClient client, Guid accountId) => operation == "disable" ? client.PostAsync($"/admin/accounts/{accountId}/disable", null) : client.DeleteAsync($"/admin/accounts/{accountId}");
            var results = await Task.WhenAll(Remove(Admin, AdminId), Remove(second, id));
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        var remaining = new[] { await Admin.GetAsync("/admin/accounts"), await second.GetAsync("/admin/accounts") };
        Assert.Single(remaining, r => r.StatusCode == HttpStatusCode.OK);
    }
    [Fact]
    public async Task Commands_report_errors_and_migrations_can_be_reapplied()
    {
        Assert.Equal(0, (await Cli(null, "migrate")).Code);
        Assert.Equal(2, (await Cli("short", "admin", "create", "invalid", "invalid@example.test")).Code);
        Assert.Equal(2, (await Cli(Password, "admin", "create", "invalid@name", "invalid@example.test")).Code);
        Assert.Equal(2, (await Cli(Password, "admin", "create", "invalid", "not an email")).Code);
        Assert.Equal(4, (await Cli(Password, "admin", "create", "OWNER", "unique@example.test")).Code);
        Assert.Equal(4, (await Cli(Password, "admin", "create", "unique", "OWNER@example.test")).Code);
        Assert.Equal(3, (await Cli(null, "admin", "grant", Guid.NewGuid().ToString())).Code);
        Assert.Equal(2, (await Cli(null, "admin", "unknown", AdminId.ToString())).Code);
        var (id, _) = await User();
        Assert.Equal(2, (await Cli(Password, "admin", "reset-password", id.ToString())).Code);
    }
}
