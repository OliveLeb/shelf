using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SessionTests : TestInstance
{
    [Theory]
    [InlineData("/account")]
    [InlineData("/spaces/personal/elements")]
    public async Task A_session_expiring_while_authentication_waits_is_not_renewed(string route)
    {
        var (id, member) = await User();
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT id FROM accounts WHERE id=$1 FOR NO KEY UPDATE", gate, transaction))
        {
            command.Parameters.AddWithValue(id);
            await command.ExecuteNonQueryAsync();
        }
        var reading = member.GetAsync(route);
        try
        {
            for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(25))
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='transactionid' AND NOT l.granted)", gate, transaction);
                if ((bool)(await command.ExecuteScalarAsync())!) break;
                Assert.True(DateTime.UtcNow < deadline, "Authentication did not reach the account lock.");
            }
            Clock.Advance(TimeSpan.FromDays(30));
        }
        finally { await transaction.CommitAsync(); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await reading).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData("targeted")]
    [InlineData("others")]
    [InlineData("all")]
    public async Task Storage_does_not_accept_a_session_revoked_after_its_initial_lookup(string operation)
    {
        var (_, member) = await User();
        using var control = await SignIn("member");
        var sessions = await Json(await member.GetAsync("/account/sessions"));
        var id = sessions.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(340035)", gate)) await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION gate_revocation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(340035); RETURN OLD; END; $$;
            CREATE TRIGGER gate_revocation BEFORE DELETE ON sessions FOR EACH ROW EXECUTE FUNCTION gate_revocation();
            """);
        var revoking = operation switch
        {
            "others" => control.PatchAsJsonAsync("/account/password", new { currentPassword = Password, password = "another-long-test-password" }),
            "all" => control.DeleteAsync("/account/sessions"),
            _ => control.DeleteAsync($"/account/sessions/{id}")
        };
        Task<HttpResponseMessage> reading;
        try
        {
            await WaitFor("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=340035 AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND NOT granted)");
            reading = member.GetAsync("/spaces/personal/elements");
            // This wait proves that the request has found the still-visible session and reached the account lock.
            await WaitFor("SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='transactionid' AND NOT l.granted)");
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(340035)", gate);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await revoking).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reading).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.PostAsJsonAsync("/folders", new { name = "forbidden" })).StatusCode);

        async Task WaitFor(string sql)
        {
            for (var deadline = DateTime.UtcNow.AddSeconds(15); ; await Task.Delay(25))
            {
                await using var command = new NpgsqlCommand(sql, gate);
                if ((bool)(await command.ExecuteScalarAsync())!) return;
                Assert.True(DateTime.UtcNow < deadline, "Session race did not reach its synchronization point.");
            }
        }
    }

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
