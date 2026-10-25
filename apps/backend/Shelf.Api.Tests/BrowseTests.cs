using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class BrowseTests : TestInstance
{
    private static async Task<Guid> Folder(HttpClient client, string name, Guid? parentId = null, string? space = null) =>
        Id(await Json(await client.PostAsJsonAsync("/folders", new { name, parentId, space })));

    private static async Task<JsonElement> Context(HttpClient client, Guid id) =>
        await Json(await client.GetAsync($"/elements/{id}/context"));

    private static void Capabilities(JsonElement value, bool download = false, bool add = false,
        bool rename = false, bool move = false, bool trash = false, bool share = false, bool copy = true)
    {
        var capabilities = value.GetProperty("capabilities");
        Assert.Equal(download, capabilities.GetProperty("canDownload").GetBoolean());
        Assert.Equal(add, capabilities.GetProperty("canAdd").GetBoolean());
        Assert.Equal(rename, capabilities.GetProperty("canRename").GetBoolean());
        Assert.Equal(move, capabilities.GetProperty("canMove").GetBoolean());
        Assert.Equal(trash, capabilities.GetProperty("canTrash").GetBoolean());
        Assert.Equal(share, capabilities.GetProperty("canShare").GetBoolean());
        Assert.Equal(copy, capabilities.GetProperty("canCopy").GetBoolean());
    }

    [Fact]
    public async Task Direct_nested_context_and_browse_return_paths_file_details_and_owner_capabilities()
    {
        var (ownerId, owner) = await User();
        var first = await Folder(owner, "First");
        var second = await Folder(owner, "Second", first);
        var current = await Folder(owner, "Current", second);
        var child = await Folder(owner, "Child", current);
        var file = await Upload(owner, "photo.jpg", current);

        var browse = await Json(await owner.GetAsync($"/spaces/personal/browse?folderId={current}"));
        var context = browse.GetProperty("context");
        Assert.Equal("personal", context.GetProperty("space").GetProperty("kind").GetString());
        Assert.Equal(current, Id(context.GetProperty("element")));
        Assert.Equal([first, second], context.GetProperty("ancestors").EnumerateArray().Select(Id));
        Assert.Equal(["First", "Second"], context.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Capabilities(context, add: true, rename: true, move: true, trash: true, share: true);
        var entries = browse.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal([child, Id(file)], entries.Select(e => Id(e.GetProperty("element"))));
        Capabilities(entries[0], add: true, rename: true, move: true, trash: true, share: true);
        Capabilities(entries[1], download: true, rename: true, move: true, trash: true, share: true);

        var detail = await Context(owner, Id(file));
        Assert.Equal([first, second, current], detail.GetProperty("ancestors").EnumerateArray().Select(Id));
        var element = detail.GetProperty("element");
        Assert.Equal("photo.jpg", element.GetProperty("name").GetString());
        Assert.Equal("file", element.GetProperty("kind").GetString());
        Assert.Equal(current, element.GetProperty("parentId").GetGuid());
        Assert.Equal(Photo.Length, element.GetProperty("size").GetInt64());
        Assert.Equal("image/jpeg", element.GetProperty("contentType").GetString());
        Assert.Equal(ownerId, Id(element.GetProperty("addedBy")));
        Assert.Equal(file.GetProperty("addedAt").GetDateTimeOffset(), element.GetProperty("addedAt").GetDateTimeOffset());
        Capabilities(detail, download: true, rename: true, move: true, trash: true, share: true);

        var root = (await Json(await owner.GetAsync("/spaces/personal/browse"))).GetProperty("context");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("element").ValueKind);
        Assert.Empty(root.GetProperty("ancestors").EnumerateArray());
        Capabilities(root, add: true, copy: false);
    }

    [Fact]
    public async Task Shared_context_hides_inaccessible_ancestors_and_capabilities_match_protected_subtree_policy()
    {
        var (_, owner) = await User();
        var (recipientId, recipient) = await User("recipient");
        var (otherId, _) = await User("other");
        var hidden = await Folder(owner, "Secret parent");
        var shared = await Folder(owner, "Shared", hidden);
        var container = await Folder(owner, "Container", shared);
        var protectedRoot = await Folder(owner, "Protected", container);
        var ordinary = await Folder(owner, "Ordinary", shared);
        var file = Id(await Upload(owner, "visible.jpg", ordinary));
        var directFile = Id(await Upload(owner, "direct.jpg", hidden));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "write" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{protectedRoot}/shares/{otherId}", new { level = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{directFile}/shares/{recipientId}", new { level = "write" })).StatusCode);

        var sharedContext = await Context(recipient, shared);
        Assert.Equal("shared", sharedContext.GetProperty("space").GetProperty("kind").GetString());
        Assert.Empty(sharedContext.GetProperty("ancestors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, sharedContext.GetProperty("element").GetProperty("parentId").ValueKind);
        Assert.DoesNotContain("Secret parent", sharedContext.GetRawText());
        Capabilities(sharedContext, add: true);
        var direct = await Context(recipient, directFile);
        Assert.Empty(direct.GetProperty("ancestors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, direct.GetProperty("element").GetProperty("parentId").ValueKind);
        Capabilities(direct, download: true);

        var protectedContext = await Context(recipient, container);
        Assert.Equal([shared], protectedContext.GetProperty("ancestors").EnumerateArray().Select(Id));
        Capabilities(protectedContext, add: true, rename: true);
        Capabilities(await Context(recipient, protectedRoot), add: true);
        Capabilities(await Context(owner, container), add: true, rename: true, move: true, trash: true, share: true);
        Capabilities(await Context(recipient, ordinary), add: true, rename: true, move: true, trash: true);
        var detail = await Context(recipient, file);
        Assert.Equal([shared, ordinary], detail.GetProperty("ancestors").EnumerateArray().Select(Id));
        Assert.DoesNotContain(hidden.ToString(), detail.GetRawText());
        Capabilities(detail, download: true, rename: true, move: true, trash: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{container}/move", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.DeleteAsync($"/elements/{container}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{shared}/rename", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await recipient.PostAsJsonAsync($"/elements/{ordinary}/rename", new { name = "Renamed" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "read" })).StatusCode);
        Capabilities(await Context(recipient, ordinary));
        Capabilities(await Context(recipient, file), download: true);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}/shares/{recipientId}")).StatusCode);
        foreach (var id in new[] { shared, ordinary, file })
        {
            var revoked = await recipient.GetAsync($"/elements/{id}/context");
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            Assert.Equal("element_not_found", await Error(revoked));
        }
        // The independent file grant remains usable, but still cannot reveal its parent.
        Capabilities(await Context(recipient, directFile), download: true);
    }

    [Fact]
    public async Task Private_context_and_browse_deny_other_accounts_and_admin_without_leaking_paths()
    {
        var (_, owner) = await User();
        var (otherId, other) = await User("other");
        var hidden = await Folder(owner, "Confidential ancestor");
        var folder = await Folder(owner, "Confidential current", hidden);
        var file = Id(await Upload(owner, "Confidential file.jpg", folder));
        foreach (var intruder in new[] { other, Admin })
        {
            foreach (var id in new[] { hidden, folder, file })
            {
                var response = await intruder.GetAsync($"/elements/{id}/context");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("{\"error\":\"element_not_found\"}", await response.Content.ReadAsStringAsync());
            }
            var browse = await intruder.GetAsync($"/spaces/personal/browse?folderId={folder}");
            Assert.Equal(HttpStatusCode.NotFound, browse.StatusCode);
            Assert.Equal("{\"error\":\"folder_not_found\"}", await browse.Content.ReadAsStringAsync());
            Assert.Empty((await Json(await intruder.GetAsync("/spaces/personal/browse"))).GetProperty("entries").EnumerateArray());
        }

        // Readability via a grant does not turn someone else's folder into a personal browse destination.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{folder}/shares/{otherId}", new { level = "write" })).StatusCode);
        Assert.Equal(folder, Id((await Context(other, folder)).GetProperty("element")));
        var sharedAsPersonal = await other.GetAsync($"/spaces/personal/browse?folderId={folder}");
        Assert.Equal(HttpStatusCode.NotFound, sharedAsPersonal.StatusCode);
        Assert.Equal("folder_not_found", await Error(sharedAsPersonal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/elements/{folder}/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/spaces/personal/browse")).StatusCode);
    }

    [Fact]
    public async Task Common_context_and_entries_are_writable_but_not_shareable_and_space_roots_only_allow_adding()
    {
        var (_, owner) = await User();
        var (_, other) = await User("other");
        var folder = await Folder(owner, "Common folder", space: "common");
        var file = Id(await Upload(owner, "common.jpg", folder));
        var personal = await Folder(owner, "Personal folder");
        var rootBrowse = await Json(await other.GetAsync("/spaces/common/browse"));
        var root = rootBrowse.GetProperty("context");
        Assert.Equal("common", root.GetProperty("space").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("element").ValueKind);
        Assert.Empty(root.GetProperty("ancestors").EnumerateArray());
        Capabilities(root, add: true, copy: false);
        var entry = Assert.Single(rootBrowse.GetProperty("entries").EnumerateArray());
        Assert.Equal(folder, Id(entry.GetProperty("element")));
        Capabilities(entry, add: true, rename: true, move: true, trash: true);
        Capabilities(await Context(other, folder), add: true, rename: true, move: true, trash: true);
        var detail = await Context(other, file);
        Assert.Equal("common", detail.GetProperty("space").GetProperty("kind").GetString());
        Assert.Equal([folder], detail.GetProperty("ancestors").EnumerateArray().Select(Id));
        Capabilities(detail, download: true, rename: true, move: true, trash: true);
        var contents = await Json(await other.GetAsync($"/spaces/common/browse?folderId={folder}"));
        Capabilities(Assert.Single(contents.GetProperty("entries").EnumerateArray()), download: true, rename: true, move: true, trash: true);

        foreach (var (space, id) in new[] { ("personal", folder), ("common", personal), ("common", file) })
        {
            var mismatch = await owner.GetAsync($"/spaces/{space}/browse?folderId={id}");
            Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
            Assert.Equal("folder_not_found", await Error(mismatch));
        }
    }

    [Fact]
    public async Task Missing_and_trashed_elements_return_404_and_invalid_browse_options_return_400()
    {
        var (_, owner) = await User();
        var (recipientId, recipient) = await User("recipient");
        var folder = await Folder(owner, "Deleted");
        var file = Id(await Upload(owner, "deleted.jpg", folder));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{folder}")).StatusCode);
        foreach (var client in new[] { owner, recipient, Admin })
        {
            foreach (var id in new[] { folder, file, Guid.NewGuid() })
            {
                var response = await client.GetAsync($"/elements/{id}/context");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("element_not_found", await Error(response));
            }
            var browse = await client.GetAsync($"/spaces/personal/browse?folderId={folder}");
            Assert.Equal(HttpStatusCode.NotFound, browse.StatusCode);
            Assert.Equal("folder_not_found", await Error(browse));
        }
        foreach (var (query, error) in new[] { ("sort=type", "invalid_sort"), ("order=up", "invalid_order") })
        {
            var invalid = await owner.GetAsync($"/spaces/personal/browse?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(error, await Error(invalid));
        }
        foreach (var space in new[] { "shared", "unknown" })
        {
            var invalid = await owner.GetAsync($"/spaces/{space}/browse");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_space", await Error(invalid));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/spaces/personal/browse?folderId=not-a-guid")).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync("/spaces/personal/browse"))).GetProperty("entries").EnumerateArray());
    }
}
