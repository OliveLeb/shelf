using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class InvitationTests : TestInstance
{
    private HttpClient RegistrationClient(string route)
    {
        var client = App.CreateClient();
        if (route == "/auth/web/register")
        {
            client.DefaultRequestHeaders.Add("Origin", "https://shelf.example.test");
            client.DefaultRequestHeaders.Add("X-Shelf-CSRF", "1");
        }
        return client;
    }

    [Theory]
    [InlineData("/auth/register")]
    [InlineData("/auth/web/register")]
    public async Task Invitation_fixes_email_and_creates_only_an_ordinary_user(string route)
    {
        using var client = RegistrationClient(route);
        var invitation = await Invite("invited@example.test");
        var response = await client.PostAsJsonAsync(route, new { token = Token(invitation), username = "invited", password = Password, email = "attacker@example.test", isAdmin = true, role = "administrator" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await Json(response);
        Assert.Equal("invited@example.test", account.GetProperty("email").GetString());
        Assert.False(account.GetProperty("isAdmin").GetBoolean());
        Assert.False(account.TryGetProperty("passwordHash", out _));
        using var member = await SignIn("INVITED@EXAMPLE.TEST");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/admin/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/admin/invitations")).StatusCode);
    }
    [Theory]
    [InlineData("missing", "/auth/register")]
    [InlineData("forged", "/auth/register")]
    [InlineData("expired", "/auth/register")]
    [InlineData("revoked", "/auth/register")]
    [InlineData("consumed", "/auth/register")]
    [InlineData("replaced", "/auth/register")]
    [InlineData("missing", "/auth/web/register")]
    [InlineData("forged", "/auth/web/register")]
    [InlineData("expired", "/auth/web/register")]
    [InlineData("revoked", "/auth/web/register")]
    [InlineData("consumed", "/auth/web/register")]
    [InlineData("replaced", "/auth/web/register")]
    public async Task Unusable_invitations_do_not_register(string reason, string route)
    {
        using var client = RegistrationClient(route);
        var link = await Invite("invitee@example.test");
        var token = Token(link);
        if (reason == "missing") token = null;
        if (reason == "forged") token = new string('0', 64);
        if (reason == "expired") { Clock.Advance(TimeSpan.FromDays(7)); Admin = await SignIn("owner"); }
        if (reason == "revoked") Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync("/admin/invitations/" + link.GetProperty("id").GetGuid())).StatusCode);
        if (reason == "consumed") Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { token, username = "first", password = Password })).StatusCode);
        if (reason == "replaced") await Invite("INVITEE@example.test");
        var response = await client.PostAsJsonAsync(route, new { token, username = "second", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_token", await Error(response));
    }
    [Theory]
    [InlineData("/auth/register")]
    [InlineData("/auth/web/register")]
    public async Task Failed_registration_keeps_invitation_usable_and_uniqueness_is_case_insensitive(string route)
    {
        using var client = RegistrationClient(route);
        var invitation = await Invite("member@example.test");
        var token = Token(invitation);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { token, username = "OWNER", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { token, username = "bad@name", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { token, username = "member", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { token, username = "member", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync("/admin/invitations", new { email = "MEMBER@example.test" })).StatusCode);
    }
    [Theory]
    [InlineData("/auth/register")]
    [InlineData("/auth/web/register")]
    public async Task Expired_invitation_can_be_reissued(string route)
    {
        using var client = RegistrationClient(route);
        await Invite("member@example.test");
        Clock.Advance(TimeSpan.FromDays(8));
        var replacement = await Invite("member@example.test");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { token = Token(replacement), username = "member", password = Password })).StatusCode);
    }
    [Theory]
    [InlineData("/auth/register")]
    [InlineData("/auth/web/register")]
    public async Task Concurrent_consumption_creates_exactly_one_account(string route)
    {
        using var client = RegistrationClient(route);
        var invitation = await Invite("member@example.test");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.PostAsJsonAsync(route, new { token = Token(invitation), username = "member" + i, password = Password })));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(2, (await Json(await Admin.GetAsync("/admin/accounts"))).GetArrayLength());
    }
    [Theory]
    [InlineData("/auth/register")]
    [InlineData("/auth/web/register")]
    public async Task Concurrent_identifier_collision_preserves_the_losing_invitation(string route)
    {
        using var client = RegistrationClient(route);
        var invitations = new[] { await Invite("one@example.test"), await Invite("two@example.test") };
        var results = await Task.WhenAll(invitations.Select(i => client.PostAsJsonAsync(route, new { token = Token(i), username = "same", password = Password })));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var loser = Array.FindIndex(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { token = Token(invitations[loser]), username = "different", password = Password })).StatusCode);
    }
}
