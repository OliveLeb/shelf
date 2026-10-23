using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class AccountStorageTests : TestInstance
{
    private static async Task<Guid> Folder(HttpClient client, string name, string? space = null) =>
        Id(await Json(await client.PostAsJsonAsync("/folders", new { name, space })));
    private async Task<string[]> Objects() => [.. ((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects ?? []).Select(o => o.Key).Order()];
    private static Task<HttpResponseMessage> DeleteSelf(HttpClient client) => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/account")
        { Content = JsonContent.Create(new { currentPassword = Password }) });
    private static async Task Download(HttpClient client, Guid id)
    {
        var link = await Json(await client.GetAsync($"/files/{id}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_purges_personal_space_and_trash_with_durable_retries_but_preserves_other_spaces(bool self)
    {
        var (ownerId, owner) = await User();
        var (recipientId, recipient) = await User("recipient");
        var folder = await Folder(owner, "Private");
        var personal = Id(await Upload(owner, "private.jpg", folder));
        var trashed = Id(await Upload(owner, "trashed.jpg", folder));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{trashed}")).StatusCode);
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "write" }));
        // Content added by somebody else still belongs to the personal space's owner.
        await Upload(recipient, "contribution.jpg", folder);
        var received = Id(await Upload(recipient, "received.jpg"));
        await Json(await recipient.PutAsJsonAsync($"/elements/{received}/shares/{ownerId}", new { level = "read" }));
        var common = await Folder(owner, "Family", "common");
        var commonFile = Id(await Upload(owner, "family.jpg", common));
        Assert.Equal(ownerId, (await Json(await recipient.GetAsync($"/folders/{common}/elements"))).EnumerateArray().Single().GetProperty("addedBy").GetProperty("id").GetGuid());
        await Eventually(async () => (await Objects()).Length == 5); // Drain temporary upload keys before denying S3 deletion.

        // The account transaction must succeed even while the S3 deletion worker cannot reach the bucket.
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:S3:SecretKey"] = "intentionally-invalid-test-key" })));
        using var deleting = await SignIn("member");
        Assert.Equal(HttpStatusCode.NoContent, (await (self ? DeleteSelf(deleting) : Admin.DeleteAsync($"/admin/accounts/{ownerId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await deleting.GetAsync("/spaces/personal/elements")).StatusCode);
        using var remaining = await SignIn("recipient");
        Assert.Empty((await Json(await remaining.GetAsync("/shares/received"))).EnumerateArray());
        Assert.Empty((await Json(await remaining.GetAsync($"/elements/{received}/shares"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await remaining.GetAsync($"/files/{personal}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await remaining.GetAsync($"/folders/{folder}/elements")).StatusCode);
        var commonFolder = Assert.Single((await Json(await remaining.GetAsync("/spaces/common/elements"))).EnumerateArray());
        Assert.Equal(common, Id(commonFolder));
        Assert.Equal(JsonValueKind.Null, commonFolder.GetProperty("addedBy").ValueKind);
        var file = Assert.Single((await Json(await remaining.GetAsync($"/folders/{common}/elements"))).EnumerateArray());
        Assert.Equal(commonFile, Id(file));
        Assert.Equal(JsonValueKind.Null, file.GetProperty("addedBy").ValueKind);
        await Eventually(() => Task.FromResult(Logs.Messages.Any(m => m.Contains("object_deletion_retry", StringComparison.Ordinal))));
        Assert.Equal(5, (await Objects()).Length);

        await RestartApp();
        Clock.Advance(TimeSpan.FromMinutes(2));
        await Eventually(async () => (await Objects()).Length == 2);
        using var survivor = await SignIn("recipient");
        await Download(survivor, received);
        await Download(survivor, commonFile);
        // Reusing the identifiers creates a genuinely new, empty personal space, without old shares.
        var (_, replacement) = await User();
        Assert.Empty(await Root(replacement));
        Assert.Empty((await Json(await replacement.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Empty((await Json(await replacement.GetAsync("/shares/received"))).EnumerateArray());
    }

    [Fact]
    public async Task Disable_preserves_personal_objects_trash_and_shares_while_revoking_the_accounts_own_access()
    {
        var (ownerId, owner) = await User();
        var (recipientId, recipient) = await User("recipient");
        var folder = await Folder(owner, "Shared");
        var file = Id(await Upload(owner, "photo.jpg", folder));
        var trash = Id(await Upload(owner, "trash.jpg"));
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "write" }));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{trash}")).StatusCode);
        await Eventually(async () => (await Objects()).Length == 2);
        var before = await Objects();
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/admin/accounts/{ownerId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/spaces/personal/elements")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync($"/files/{file}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/uploads", new { name = "forbidden", size = 1 })).StatusCode);
        var share = Assert.Single((await Json(await recipient.GetAsync("/shares/received"))).EnumerateArray());
        Assert.Equal(ownerId, share.GetProperty("owner").GetProperty("id").GetGuid());
        await Download(recipient, file);
        Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(before, await Objects());
        // Write access also survives: the disabled owner's content is still collaborative.
        await Upload(recipient, "later.jpg", folder);
        Assert.Equal(2, (await Json(await recipient.GetAsync($"/folders/{folder}/elements"))).GetArrayLength());
        // A subsequent administrative deletion must still purge the disabled account's trash and new contributions.
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{ownerId}")).StatusCode);
        await Eventually(async () => (await Objects()).Length == 0);
    }

    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "self")]
    [InlineData(true, "self")]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    public async Task Confirmation_during_account_removal_is_refused_and_pending_objects_are_eventually_purged(bool common, string operation)
    {
        var (id, owner) = await User();
        var folder = await Folder(owner, "Destination", common ? "common" : null);
        var pending = await Declare(owner, "pending.jpg", Photo.Length, parentId: folder);
        await Put(pending, Photo);
        // Hold deletion after it locks the account but before session/space cascades run.
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(150015)", gate)) await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION gate_account_deletion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(150015);
                IF TG_OP='UPDATE' THEN RETURN NEW; ELSE RETURN OLD; END IF;
            END; $$;
            CREATE TRIGGER gate_account_deletion BEFORE DELETE ON accounts FOR EACH ROW EXECUTE FUNCTION gate_account_deletion();
            CREATE TRIGGER gate_account_disable BEFORE UPDATE OF is_active ON accounts FOR EACH ROW EXECUTE FUNCTION gate_account_deletion();
            """);
        var deletion = operation switch
        {
            "self" => DeleteSelf(owner),
            "disable" => Admin.PostAsync($"/admin/accounts/{id}/disable", null),
            _ => Admin.DeleteAsync($"/admin/accounts/{id}")
        };
        Task<HttpResponseMessage> confirmation;
        try
        {
            await Eventually(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=150015 AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND NOT granted)", gate);
                return (bool)(await command.ExecuteScalarAsync())!;
            });
            confirmation = Confirm(owner, pending);
            // Wait for an actual blocked transaction, rather than depending on request timing.
            await Eventually(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='transactionid' AND NOT l.granted)", gate);
                return (bool)(await command.ExecuteScalarAsync())!;
            });
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(150015)", gate);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await deletion).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await confirmation).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(owner, pending)).StatusCode);
        Assert.DoesNotContain((await Json(await Admin.GetAsync("/spaces/common/elements"))).EnumerateArray(), e => e.GetProperty("kind").GetString() == "file");
        if (common) Assert.Empty((await Json(await Admin.GetAsync($"/folders/{folder}/elements"))).EnumerateArray());
        // A previously signed URL may still finish transferring after deletion; retain cleanup metadata until expiry.
        await Put(pending, Photo);
        Clock.Advance(TimeSpan.FromHours(24));
        await Eventually(async () => (await Objects()).Length == 0);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(15); !await condition(); await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "Account storage work did not finish within 15 seconds.");
    }
}
