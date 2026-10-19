using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SecretTests : TestInstance
{
    [Fact]
    public async Task Consultation_and_logs_do_not_disclose_passwords_tokens_or_hashes()
    {
        var invitation = await Invite("member@example.test");
        var invitationToken = Token(invitation);
        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync("/auth/register", new { token = invitationToken, username = "member", password = Password })).StatusCode);
        var login = await Client.PostAsJsonAsync("/auth/login", new { identifier = "member", password = Password });
        var sessionToken = (await Json(login)).GetProperty("token").GetString()!;
        using var member = await SignIn("member");
        var id = (await Json(await member.GetAsync("/account"))).GetProperty("id").GetGuid();
        var reset = await Json(await Admin.PostAsync($"/admin/accounts/{id}/password-reset", null));
        foreach (var response in new[] { await member.GetAsync("/account"), await member.GetAsync("/account/sessions"), await Admin.GetAsync("/admin/accounts"), await Admin.GetAsync("/admin/invitations") })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var body = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { Password, invitationToken, sessionToken, Token(reset), "passwordHash", "tokenHash" })
                Assert.DoesNotContain(secret, body);
        }
        var logs = string.Join('\n', Logs.Messages);
        foreach (var secret in new[] { Password, invitationToken, sessionToken, Token(reset) }) Assert.DoesNotContain(secret, logs);
        // Cookies cannot authenticate a browser; the explicit bearer header is required.
        using var cookieClient = App.CreateClient();
        cookieClient.DefaultRequestHeaders.Add("Cookie", "session=" + sessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookieClient.GetAsync("/account")).StatusCode);
    }
}
