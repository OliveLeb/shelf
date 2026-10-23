using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class TrashTests : TestInstance
{
    private static async Task<Guid> Folder(HttpClient client, string name, Guid? parentId = null, string? space = null) =>
        Id(await Json(await client.PostAsJsonAsync("/folders", new { name, parentId, space })));
    private static async Task<JsonElement[]> Trash(HttpClient client, string space = "personal") =>
        [.. (await Json(await client.GetAsync($"/spaces/{space}/trash"))).EnumerateArray()];
    private static Task<HttpResponseMessage> Restore(HttpClient client, Guid id) => client.PostAsync($"/trash/{id}/restore", null);
    private static Task<HttpResponseMessage> Delete(HttpClient client, Guid id) => client.DeleteAsync($"/elements/{id}");
    private static async Task<JsonElement[]> Content(HttpClient client, Guid id) =>
        [.. (await Json(await client.GetAsync($"/folders/{id}/elements"))).EnumerateArray()];
    private async Task<int> ObjectCount() => (await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects?.Count ?? 0;

    [Fact]
    public async Task Deleted_folder_is_hidden_with_its_contents_and_shares_then_restored_intact_at_its_origin()
    {
        var (ownerId, owner) = await User();
        var (readerId, reader) = await User("reader");
        var parent = await Folder(owner, "Albums");
        var folder = await Folder(owner, "Holiday", parent);
        var nested = await Folder(owner, "Day one", folder);
        var file = Id(await Upload(owner, "photo.jpg", nested));
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{readerId}", new { level = "read" }));
        await Json(await owner.PutAsJsonAsync($"/elements/{file}/shares/{readerId}", new { level = "read" }));

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(owner, folder)).StatusCode);
        Assert.Empty(await Content(owner, parent));
        foreach (var client in new[] { owner, reader })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/folders/{nested}/elements")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{file}/download")).StatusCode);
        }
        Assert.Empty((await Json(await reader.GetAsync("/shares/received"))).EnumerateArray());
        var entry = Assert.Single(await Trash(owner));
        Assert.Equal(folder, Id(entry));
        Assert.Equal(parent, entry.GetProperty("originalParentId").GetGuid());
        Assert.Equal("/Albums", entry.GetProperty("originalPath").GetString());
        Assert.InRange(entry.GetProperty("deletedAt").GetDateTimeOffset(), Clock.GetUtcNow().AddMicroseconds(-1), Clock.GetUtcNow());
        Assert.Equal(ownerId, entry.GetProperty("deletedBy").GetProperty("id").GetGuid());

        var restored = await Json(await Restore(owner, folder));
        Assert.Equal(parent, restored.GetProperty("parentId").GetGuid());
        Assert.Empty(await Trash(owner));
        Assert.Equal(folder, Id(Assert.Single(await Content(owner, parent))));
        Assert.Equal(nested, Id(Assert.Single(await Content(owner, folder))));
        Assert.Equal(file, Id(Assert.Single(await Content(reader, nested))));
        var link = await Json(await owner.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Replace_preserves_the_file_and_its_shares_and_trashes_old_bytes_only_after_valid_confirmation()
    {
        var (_, owner) = await User();
        var (readerId, reader) = await User("reader");
        var original = Id(await Upload(owner, "photo.jpg"));
        await Json(await owner.PutAsJsonAsync($"/elements/{original}/shares/{readerId}", new { level = "read" }));
        byte[] replacement = "new content"u8.ToArray();
        var pending = await Declare(owner, "PHOTO.jpg", replacement.Length, onConflict: "replace");
        Assert.Empty(await Trash(owner));
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(owner, pending)).StatusCode);
        Assert.Empty(await Trash(owner));
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, replacement)).StatusCode);
        Assert.Equal(original, Id(await Json(await Confirm(owner, pending))));
        Assert.Equal(original, Id(Assert.Single(await Root(owner))));
        var entry = Assert.Single(await Trash(owner));
        Assert.NotEqual(original, Id(entry));
        Assert.Equal("photo.jpg", entry.GetProperty("element").GetProperty("name").GetString());
        var link = await Json(await reader.GetAsync($"/files/{original}/download"));
        Assert.Equal(replacement, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));

        var restored = await Json(await Restore(owner, Id(entry)));
        Assert.Equal("photo (1).jpg", restored.GetProperty("name").GetString());
        var oldLink = await Json(await owner.GetAsync($"/files/{Id(restored)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(oldLink.GetProperty("url").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/files/{Id(restored)}/download")).StatusCode);
    }

    [Fact]
    public async Task Permanent_purge_removes_metadata_even_when_S3_denies_deletion_then_retries_without_losing_the_objects()
    {
        var (_, owner) = await User();
        var folder = await Folder(owner, "Remove");
        await Upload(owner, "a.jpg", folder);
        await Upload(owner, "b.jpg", folder);
        var kept = Id(await Upload(owner, "keep.jpg"));
        await Eventually(async () => await ObjectCount() == 3); // Drain temporary keys before making S3 unavailable.
        // Real MinIO rejects this host's credentials; the fixture retains working credentials to inspect the bucket.
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:S3:SecretKey"] = "intentionally-invalid-test-key" })));
        using var failingHost = await SignIn("member");
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(failingHost, folder)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await failingHost.DeleteAsync($"/trash/{folder}")).StatusCode);
        Assert.Empty(await Trash(failingHost));
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(failingHost, folder)).StatusCode);
        await Eventually(() => Task.FromResult(Logs.Messages.Any(m => m.Contains("object_deletion_retry", StringComparison.Ordinal))));
        Assert.Equal(3, await ObjectCount());

        // Recreate the entire HTTP host: pending deletion must survive in PostgreSQL, not in worker memory.
        await RestartApp();
        Clock.Advance(TimeSpan.FromMinutes(2));
        await Eventually(async () => await ObjectCount() == 1);
        using var signedIn = await SignIn("member");
        var link = await Json(await signedIn.GetAsync($"/files/{kept}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_PUT_after_purge_is_collected_without_deleting_live_trashed_or_pending_content(bool confirmed)
    {
        var (_, owner) = await User();
        var folder = await Folder(owner, "Remove");
        var ticket = await Declare(owner, "late.jpg", Photo.Length, parentId: folder);
        Assert.Equal(HttpStatusCode.OK, (await Put(ticket, Photo)).StatusCode);
        if (confirmed) await Json(await Confirm(owner, ticket));
        var kept = Id(await Upload(owner, "keep.jpg", content: "live"u8.ToArray()));
        var trashed = Id(await Upload(owner, "restore.jpg", content: "trashed"u8.ToArray()));
        await Delete(owner, trashed);
        await Delete(owner, folder);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/trash/{folder}")).StatusCode);
        if (!confirmed)
        {
            // Unconfirmed content is retained until the upload expires, even after its destination is purged.
            await Eventually(async () => await ObjectCount() == 3);
            Clock.Advance(TimeSpan.FromHours(24));
        }
        await Eventually(async () => await ObjectCount() == 2);
        var pending = await Declare(owner, "pending.jpg", Photo.Length);
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, Photo)).StatusCode);
        // Let the successful DELETE be acknowledged before replaying the still-valid write capability.
        await Task.Delay(1500);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Put(ticket, Photo)).StatusCode);
            Assert.Equal(4, await ObjectCount());
            Clock.Advance(TimeSpan.FromMinutes(2));
            await Eventually(async () => await ObjectCount() == 3);
            await Task.Delay(1500);
        }

        Assert.Equal(trashed, Id(Assert.Single(await Trash(owner))));
        await Json(await Restore(owner, trashed));
        var completed = Id(await Json(await Confirm(owner, pending)));
        foreach (var (id, bytes) in new[] { (kept, "live"u8.ToArray()), (trashed, "trashed"u8.ToArray()), (completed, Photo) })
        {
            var link = await Json(await owner.GetAsync($"/files/{id}/download"));
            Assert.Equal(bytes, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(owner, folder)).StatusCode);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("trashed")]
    [InlineData("purged")]
    [InlineData("common")]
    public async Task Restoration_uses_the_original_folder_only_if_still_active_in_the_same_space_and_renames_conflicts(string origin)
    {
        var (_, owner) = await User();
        var parent = await Folder(owner, "Albums");
        var file = Id(await Upload(owner, "photo.jpg", parent));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(owner, file)).StatusCode);
        Guid? destination = parent;
        if (origin is "trashed" or "purged")
        {
            Assert.Equal(HttpStatusCode.NoContent, (await Delete(owner, parent)).StatusCode);
            if (origin == "purged") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/trash/{parent}")).StatusCode);
            destination = null;
        }
        if (origin == "common")
        {
            await Json(await owner.PostAsJsonAsync($"/elements/{parent}/move", new { space = "common" }));
            destination = null;
        }
        await Upload(owner, "PHOTO.jpg", destination);
        await Upload(owner, "photo (1).jpg", destination);
        var restored = await Json(await Restore(owner, file));
        Assert.Equal(file, Id(restored));
        Assert.Equal("photo (2).jpg", restored.GetProperty("name").GetString());
        Assert.Equal(destination, restored.GetProperty("parentId").Deserialize<Guid?>());
        if (origin == "trashed")
        {
            await Json(await Restore(owner, parent));
            Assert.Empty(await Content(owner, parent));
        }
    }

    [Fact]
    public async Task Personal_trash_is_private_even_for_a_reader_or_administrator_and_common_trash_is_managed_by_every_user()
    {
        var (_, owner) = await User();
        var (readerId, reader) = await User("reader");
        var folder = await Folder(owner, "Private");
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{readerId}", new { level = "read" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(reader, folder)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(owner, folder)).StatusCode);
        foreach (var intruder in new[] { reader, Admin })
        {
            Assert.Empty(await Trash(intruder));
            Assert.Equal(HttpStatusCode.NotFound, (await Restore(intruder, folder)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await intruder.DeleteAsync($"/trash/{folder}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await intruder.DeleteAsync("/spaces/personal/trash")).StatusCode);
        }
        Assert.Equal(folder, Id(Assert.Single(await Trash(owner))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/spaces/common/trash")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.DeleteAsync($"/trash/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync("/spaces/unknown/trash")).StatusCode);

        var common = await Folder(owner, "Common", space: "common");
        var file = Id(await Upload(owner, "shared.jpg", common));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(reader, common)).StatusCode);
        var entry = Assert.Single(await Trash(Admin, "common"));
        Assert.Equal(readerId, entry.GetProperty("deletedBy").GetProperty("id").GetGuid());
        await Json(await Restore(Admin, common));
        Assert.Equal(file, Id(Assert.Single(await Content(reader, common))));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(Admin, common)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/spaces/common/trash")).StatusCode);
        Assert.Empty(await Trash(reader, "common"));
        await Eventually(async () => await ObjectCount() == 0);
    }

    [Fact]
    public async Task Expiry_purges_at_30_days_but_not_before_and_leaves_younger_trash_and_restored_files_untouched()
    {
        var (_, owner) = await User();
        var folder = await Folder(owner, "Expired");
        await Upload(owner, "a.jpg", folder);
        await Upload(owner, "b.jpg", folder);
        var probe = Id(await Upload(owner, "restorable.jpg"));
        await Delete(owner, folder);
        await Delete(owner, probe);
        Clock.Advance(TimeSpan.FromDays(29));
        using var day29 = await SignIn("member");
        var young = Id(await Upload(day29, "young.jpg"));
        await Delete(day29, young);
        Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
        using var before = await SignIn("member");
        Assert.Equal(3, (await Trash(before)).Length);
        Assert.Equal(probe, Id(await Json(await Restore(before, probe))));
        await Eventually(async () => await ObjectCount() == 4);

        Clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(async () => (await Trash(before)).Length == 1);
        Assert.Equal(young, Id(Assert.Single(await Trash(before))));
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(before, folder)).StatusCode);
        await Eventually(async () => await ObjectCount() == 2);
        Assert.Equal(young, Id(await Json(await Restore(before, young))));
        Assert.Equal(2, (await Root(before)).Length);
    }

    [Fact]
    public async Task Confirmation_into_a_trashed_folder_is_refused_and_purge_also_cleans_transferred_pending_uploads()
    {
        var (_, owner) = await User();
        var folder = await Folder(owner, "Pending");
        var pending = await Declare(owner, "pending.jpg", Photo.Length, parentId: folder);
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, Photo)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(owner, folder)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(owner, pending)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/folders", new { name = "late", parentId = folder })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/spaces/personal/trash")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(owner, pending)).StatusCode);
        // The upload survives its destination until issued URLs expire, then its objects are purged.
        Clock.Advance(TimeSpan.FromHours(24));
        await Eventually(async () => await ObjectCount() == 0);
    }

    [Fact]
    public async Task Replace_rechecks_the_current_name_at_confirmation_and_never_replaces_a_folder()
    {
        var (_, owner) = await User();
        // A free name at declaration may be taken before confirmation.
        var pending = await Declare(owner, "late.jpg", Photo.Length, onConflict: "replace");
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, Photo)).StatusCode);
        var existing = Id(await Upload(owner, "late.jpg", content: "older"u8.ToArray()));
        Assert.Equal(existing, Id(await Json(await Confirm(owner, pending))));
        Assert.Single(await Trash(owner));

        var blocked = await Declare(owner, "Folder", Photo.Length, onConflict: "replace");
        Assert.Equal(HttpStatusCode.OK, (await Put(blocked, Photo)).StatusCode);
        var folder = await Folder(owner, "FOLDER");
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(owner, blocked)).StatusCode);
        Assert.Single(await Trash(owner));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/uploads", new { name = "folder", size = 1, onConflict = "replace" })).StatusCode);
        Assert.Empty(await Content(owner, folder));

        var fresh = await Upload(owner, "fresh.jpg", onConflict: "replace");
        Assert.Contains(await Root(owner), e => Id(e) == Id(fresh));
        Assert.Single(await Trash(owner));
    }

    [Fact]
    public async Task Concurrent_restorations_of_the_same_name_both_succeed_without_duplicate_names_or_lost_bytes()
    {
        var (_, owner) = await User();
        var first = Id(await Upload(owner, "same.jpg", content: "first"u8.ToArray()));
        await Delete(owner, first);
        var second = Id(await Upload(owner, "same.jpg", content: "second"u8.ToArray()));
        await Delete(owner, second);
        using var device = await SignIn("member");
        var restored = await Task.WhenAll(Restore(owner, first), Restore(device, second));
        Assert.All(restored, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(["same (1).jpg", "same.jpg"], (await Root(owner)).Select(e => e.GetProperty("name").GetString()).Order());
        Assert.Empty(await Trash(owner));
        foreach (var (id, bytes) in new[] { (first, "first"), (second, "second") })
        {
            var link = await Json(await owner.GetAsync($"/files/{id}/download"));
            Assert.Equal(bytes, await Transfer.GetStringAsync(link.GetProperty("url").GetString()));
        }
    }

    private async Task Eventually(Func<Task<bool>> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        do
        {
            if (await condition()) return;
            await Task.Delay(100);
        } while (DateTime.UtcNow < timeout);
        Assert.Fail("Background storage work did not finish within 15 seconds. " + string.Join('\n', Logs.Messages.Where(m => m.Contains("retry", StringComparison.Ordinal))));
    }
}
