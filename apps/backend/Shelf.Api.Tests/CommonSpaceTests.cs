using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class CommonSpaceTests : TestInstance
{
    private static async Task<JsonElement[]> Common(HttpClient client) =>
        [.. (await Json(await client.GetAsync("/spaces/common/elements"))).EnumerateArray()];

    private static async Task<JsonElement> CommonFolder(HttpClient client, string name, Guid? parentId = null) =>
        await Json(await client.PostAsJsonAsync("/folders", new { name, parentId, space = "common" }));

    private static async Task<JsonElement> CommonUpload(HttpClient client, string name, Guid? parentId = null)
    {
        var ticket = await Json(await client.PostAsJsonAsync("/uploads", new { name, size = Photo.Length, parentId, space = "common" }));
        Assert.Equal(HttpStatusCode.OK, (await Put(ticket, Photo)).StatusCode);
        return await Json(await Confirm(client, ticket));
    }

    [Fact]
    public async Task Common_names_are_unique_and_keep_both_handles_conflicts_at_declaration_and_confirmation()
    {
        var (_, member) = await User();
        var (_, other) = await User("other");
        var folder = Id(await CommonFolder(member, "Photos"));
        var file = Id(await CommonUpload(member, "photo.jpg"));
        Assert.Equal("name_conflict", await Error(await other.PostAsJsonAsync("/folders", new { name = "PHOTO.JPG", space = "common" })));
        Assert.Equal("name_conflict", await Error(await other.PostAsJsonAsync("/uploads", new { name = "photos", size = 1, space = "common" })));
        Assert.Equal("name_conflict", await Error(await other.PostAsJsonAsync($"/elements/{file}/rename", new { name = "PHOTOS" })));
        var inside = Id(await CommonUpload(other, "PHOTO.jpg", folder));
        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync($"/elements/{inside}/move", new { space = "common" })));

        async Task<JsonElement> Ticket(string name, string? onConflict = null) =>
            await Json(await other.PostAsJsonAsync("/uploads", new { name, size = Photo.Length, space = "common", onConflict }));
        var keep = await Ticket("photo.jpg", "keepBoth");
        Assert.Equal(HttpStatusCode.OK, (await Put(keep, Photo)).StatusCode);
        Assert.Equal("photo (1).jpg", (await Json(await Confirm(other, keep))).GetProperty("name").GetString());
        var late = await Ticket("late.jpg");
        var lateKeep = await Ticket("late.jpg", "keepBoth");
        Assert.Equal(HttpStatusCode.OK, (await Put(late, Photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(lateKeep, Photo)).StatusCode);
        await CommonUpload(member, "late.jpg");
        Assert.Equal("name_conflict", await Error(await Confirm(other, late)));
        Assert.Equal("late (1).jpg", (await Json(await Confirm(other, lateKeep))).GetProperty("name").GetString());

        // A failed transfer leaves the whole personal subtree and pending uploads private.
        var personal = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "PHOTOS" })));
        var child = Id(await Upload(member, "private.jpg", personal));
        var pending = await Declare(member, "pending.jpg", Photo.Length, parentId: personal);
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, Photo)).StatusCode);
        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync($"/elements/{personal}/move", new { space = "common" })));
        await Json(await Confirm(member, pending));
        Assert.Equal(personal, Id(Assert.Single(await Root(member))));
        Assert.Equal(2, (await Json(await member.GetAsync($"/folders/{personal}/elements"))).GetArrayLength());
        Assert.Equal("element_not_found", await Error(await other.GetAsync($"/files/{child}/download")));
        Assert.Equal("folder_not_found", await Error(await other.GetAsync($"/folders/{personal}/elements")));
        // A private root name is independent of the common root name.
        await Upload(other, "photo.jpg");
        Assert.Single(await Root(other));
        Assert.Equal("invalid_space", await Error(await member.PostAsJsonAsync("/folders", new { name = "X", space = "unknown" })));
        Assert.Equal("invalid_space", await Error(await member.PostAsJsonAsync("/uploads", new { name = "X", size = 1, space = "unknown" })));
        Assert.Equal("invalid_space", await Error(await member.PostAsJsonAsync($"/elements/{file}/move", new { space = "unknown" })));
        Assert.Equal("folder_not_found", await Error(await member.PostAsJsonAsync("/folders", new { name = "X", parentId = personal, space = "common" })));
        Assert.Equal("folder_not_found", await Error(await member.PostAsJsonAsync("/uploads", new { name = "X", size = 1, parentId = folder, space = "personal" })));
    }

    [Fact]
    public async Task Common_elements_survive_the_deletion_of_the_user_who_added_them()
    {
        var (memberId, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Albums" })));
        await Json(await member.PostAsJsonAsync($"/elements/{folder}/move", new { space = "common" }));
        var file = Id(await CommonUpload(member, "photo.jpg", folder));
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{memberId}")).StatusCode);
        var (_, other) = await User("other");
        Assert.Equal(JsonValueKind.Null, Assert.Single(await Common(other)).GetProperty("addedBy").ValueKind);
        var content = await Json(await other.GetAsync($"/folders/{folder}/elements"));
        Assert.Equal(JsonValueKind.Null, Assert.Single(content.EnumerateArray()).GetProperty("addedBy").ValueKind);
        var download = await Json(await other.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Concurrent_common_writes_preserve_unique_names_and_moves_never_form_a_cycle()
    {
        var (_, member) = await User();
        var (_, other) = await User("other");
        var uploads = new[]
        {
            await Json(await member.PostAsJsonAsync("/uploads", new { name = "same.jpg", size = Photo.Length, space = "common" })),
            await Json(await other.PostAsJsonAsync("/uploads", new { name = "SAME.jpg", size = Photo.Length, space = "common" }))
        };
        foreach (var upload in uploads) Assert.Equal(HttpStatusCode.OK, (await Put(upload, Photo)).StatusCode);
        var results = await Task.WhenAll(Confirm(member, uploads[0]), Confirm(other, uploads[1]));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        Assert.Equal("name_conflict", await Error(results.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        for (var round = 0; round < 5; round++)
        {
            var x = Id(await CommonFolder(member, $"X{round}"));
            var y = Id(await CommonFolder(other, $"Y{round}"));
            var moves = await Task.WhenAll(
                member.PostAsJsonAsync($"/elements/{x}/move", new { parentId = y }),
                other.PostAsJsonAsync($"/elements/{y}/move", new { parentId = x }));
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], moves.Select(m => m.StatusCode).Order());
            Assert.Equal("folder_cycle", await Error(moves.Single(m => m.StatusCode == HttpStatusCode.Conflict)));
        }
        Assert.Equal(6, (await Common(member)).Length);
    }

    [Fact]
    public async Task Concurrent_transfer_confirmation_and_folder_creation_keep_all_content_in_common()
    {
        var (_, member) = await User();
        using var phone = await SignIn("member");
        using var tablet = await SignIn("member");
        var (_, other) = await User("other");
        for (var round = 0; round < 5; round++)
        {
            var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = $"Albums{round}" })));
            var pending = await Declare(member, "photo.jpg", Photo.Length, parentId: folder);
            Assert.Equal(HttpStatusCode.OK, (await Put(pending, Photo)).StatusCode);
            var results = await Task.WhenAll(
                member.PostAsJsonAsync($"/elements/{folder}/move", new { space = "common" }),
                Confirm(phone, pending),
                tablet.PostAsJsonAsync("/folders", new { name = "Summer", parentId = folder }));
            foreach (var result in results) await Json(result);
            var content = await Json(await other.GetAsync($"/folders/{folder}/elements"));
            Assert.Equal(["Summer", "photo.jpg"], content.EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        }
        Assert.Empty(await Root(member));
        Assert.Equal(5, (await Common(other)).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_owner_can_transfer_personal_elements_to_common_without_changing_S3_objects(bool folder)
    {
        var (memberId, member) = await User();
        var (_, other) = await User("other");
        Guid? nested = null;
        Guid? root = null;
        JsonElement? pending = null;
        if (folder)
        {
            root = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Albums" })));
            nested = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Summer", parentId = root })));
            pending = await Declare(member, "pending.jpg", Photo.Length, parentId: nested);
            Assert.Equal(HttpStatusCode.OK, (await Put(pending.Value, Photo)).StatusCode);
        }
        var file = await Upload(member, "photo.jpg", nested);
        root ??= Id(file);
        async Task<string[]> Objects() => [.. ((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects ?? [])
            .Select(o => $"{o.Key}:{o.ETag}:{o.LastModified:O}").Order(StringComparer.Ordinal)];
        // Compare stable objects after the confirmed upload's temporary key has been drained.
        for (var deadline = DateTime.UtcNow.AddSeconds(15); (await Objects()).Length != (folder ? 2 : 1); await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "Temporary upload key was not cleaned up");
        var before = await Objects();
        var destination = Id(await CommonFolder(other, "Destination"));
        foreach (var intruder in new[] { other, Admin })
            Assert.Equal("element_not_found", await Error(await intruder.PostAsJsonAsync($"/elements/{root}/move", new { space = "common", parentId = destination })));

        var moved = await Json(await member.PostAsJsonAsync($"/elements/{root}/move", new { space = "common", parentId = destination }));
        Assert.Equal(destination, moved.GetProperty("parentId").GetGuid());
        Assert.Equal(memberId, moved.GetProperty("addedBy").GetProperty("id").GetGuid());
        Assert.Empty(await Root(member));
        Assert.Equal(root, Id(Assert.Single((await Json(await other.GetAsync($"/folders/{destination}/elements"))).EnumerateArray())));
        Assert.Equal(before, await Objects()); // Moving preserves objects; confirming the pending upload below publishes a new key.
        if (nested is not null)
        {
            Assert.Equal(nested, Id(Assert.Single((await Json(await other.GetAsync($"/folders/{root}/elements"))).EnumerateArray())));
            Assert.Equal(Id(file), Id(Assert.Single((await Json(await other.GetAsync($"/folders/{nested}/elements"))).EnumerateArray())));
            var confirmed = await Json(await Confirm(member, pending!.Value));
            Assert.Equal(memberId, confirmed.GetProperty("addedBy").GetProperty("id").GetGuid());
            Assert.Equal(2, (await Json(await other.GetAsync($"/folders/{nested}/elements"))).GetArrayLength());
            for (var deadline = DateTime.UtcNow.AddSeconds(15); (await Objects()).Length != 2; await Task.Delay(50))
                Assert.True(DateTime.UtcNow < deadline, "Temporary upload key was not cleaned up");
            before = await Objects();
        }
        var download = await Json(await other.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        // Explicit common root and inferred common root both preserve the element's common space.
        await Json(await other.PostAsJsonAsync($"/elements/{root}/move", new { space = "common" }));
        await Json(await other.PostAsJsonAsync($"/elements/{root}/move", new { parentId = (Guid?)null }));
        var personalFolder = Id(await Json(await other.PostAsJsonAsync("/folders", new { name = "Private" })));
        foreach (var element in new[] { root.Value, Id(file) }.Distinct())
        {
            foreach (var actor in new[] { member, other, Admin })
            {
                var refused = await actor.PostAsJsonAsync($"/elements/{element}/move", new { space = "personal" });
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("common_to_personal_forbidden", await Error(refused));
            }
            var intoPersonal = await other.PostAsJsonAsync($"/elements/{element}/move", new { parentId = personalFolder });
            Assert.Equal(HttpStatusCode.Forbidden, intoPersonal.StatusCode);
        }
        Assert.Equal(2, (await Common(other)).Length);
        Assert.Equal(before, await Objects());
    }

    [Fact]
    public async Task Any_user_can_upload_download_rename_and_move_elements_added_by_another_user()
    {
        var (memberId, member) = await User();
        var (_, other) = await User("other");
        var folder = Id(await CommonFolder(member, "Photos"));
        var nested = Id(await CommonFolder(other, "Summer", folder));
        var file = await CommonUpload(member, "photo.jpg");
        var id = Id(file);
        await Json(await other.PostAsJsonAsync($"/elements/{id}/rename", new { name = "summer.jpg" }));
        await Json(await other.PostAsJsonAsync($"/elements/{id}/move", new { parentId = nested }));
        await Json(await other.PostAsJsonAsync($"/elements/{folder}/rename", new { name = "Albums" }));
        var content = await Json(await other.GetAsync($"/folders/{nested}/elements"));
        var visible = Assert.Single(content.EnumerateArray());
        Assert.Equal(memberId, visible.GetProperty("addedBy").GetProperty("id").GetGuid());
        Assert.Equal("summer.jpg", visible.GetProperty("name").GetString());
        var download = await Json(await other.GetAsync($"/files/{id}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        // No destination means the root of the element's current space.
        await Json(await other.PostAsJsonAsync($"/elements/{id}/move", new { parentId = (Guid?)null }));
        Assert.Equal(2, (await Common(other)).Length);
        Assert.Empty(await Root(other));
        Assert.Equal(0, (await Cli(null, "migrate")).Code);
        Assert.Equal(2, (await Common(member)).Length);
    }

    [Fact]
    public async Task Every_active_user_sees_the_same_common_space_and_the_user_who_added_each_element()
    {
        var (memberId, member) = await User();
        Assert.Empty(await Common(member));
        var folder = await Json(await member.PostAsJsonAsync("/folders", new { name = "Photos", space = "common" }));
        var (_, other) = await User("other");
        foreach (var viewer in new[] { member, other, Admin })
        {
            var visible = Assert.Single(await Common(viewer));
            Assert.Equal(Id(folder), Id(visible));
            Assert.Equal(memberId, visible.GetProperty("addedBy").GetProperty("id").GetGuid());
            Assert.False(visible.TryGetProperty("owner", out _));
            Assert.Empty(await Root(viewer));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/spaces/common/elements")).StatusCode);
        await Json(await other.GetAsync("/spaces/common/elements?sort=addedAt&order=desc"));
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/admin/accounts/{memberId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/spaces/common/elements")).StatusCode);
        Assert.Equal(Id(folder), Id(Assert.Single(await Common(other))));
    }
}
