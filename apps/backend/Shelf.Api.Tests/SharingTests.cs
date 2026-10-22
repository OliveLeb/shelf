using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SharingTests : TestInstance
{
    private static Task<HttpResponseMessage> Share(HttpClient owner, Guid element, Guid recipient, string level = "read") =>
        owner.PutAsJsonAsync($"/elements/{element}/shares/{recipient}", new { level });
    private static async Task<JsonElement[]> Shared(HttpClient recipient) =>
        [.. (await Json(await recipient.GetAsync("/shares/received"))).EnumerateArray()];

    [Fact]
    public async Task Write_recipient_organizes_contents_but_not_shared_roots_and_owner_controls_deleted_contents()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var (_, otherOwner) = await User("charlie");
        var source = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Source" })));
        var destination = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Destination" })));
        var readOnly = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Read only" })));
        var otherSpace = Id(await Json(await otherOwner.PostAsJsonAsync("/folders", new { name = "Other owner" })));
        foreach (var folder in new[] { source, destination })
            await Json(await Share(owner, folder, recipientId, "write"));
        await Json(await Share(owner, readOnly, recipientId));
        await Json(await Share(otherOwner, otherSpace, recipientId, "write"));
        var nested = Id(await Json(await recipient.PostAsJsonAsync("/folders", new { name = "Nested", parentId = source })));
        var file = Id(await Upload(recipient, "new.jpg", nested));
        await Json(await recipient.PostAsJsonAsync($"/elements/{file}/rename", new { name = "renamed.jpg" }));
        await Json(await recipient.PostAsJsonAsync($"/elements/{file}/move", new { parentId = destination }));
        var moved = Assert.Single((await Json(await owner.GetAsync($"/folders/{destination}/elements"))).EnumerateArray());
        Assert.Equal(file, Id(moved));
        Assert.Equal(recipientId, Id(moved.GetProperty("addedBy")));
        Assert.Empty(await Root(recipient));
        foreach (var target in new[] { readOnly, otherSpace })
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{file}/move", new { parentId = target })).StatusCode);
        foreach (var space in new[] { "personal", "common" })
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{file}/move", new { space })).StatusCode);

        // A direct read grant must not reduce inherited write, but makes this element a protected root.
        await Json(await Share(owner, file, recipientId));
        foreach (var root in new[] { source, file })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{root}/rename", new { name = "Forbidden" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{root}/move", new { parentId = destination })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.DeleteAsync($"/elements/{root}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.GetAsync($"/elements/{root}/shares")).StatusCode);
        }
        await Json(await Share(owner, file, recipientId, "write"));
        Assert.Equal("write", (await Shared(recipient)).Single(e => Id(e.GetProperty("element")) == file).GetProperty("level").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{file}/shares/{recipientId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await recipient.DeleteAsync($"/elements/{file}")).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync($"/folders/{destination}/elements"))).EnumerateArray());
        Assert.Empty((await Json(await recipient.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        var deleted = Assert.Single((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal(file, Id(deleted));
        Assert.Equal(recipientId, Id(deleted.GetProperty("deletedBy")));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.PostAsync($"/trash/{file}/restore", null)).StatusCode);
        await Json(await owner.PostAsync($"/trash/{file}/restore", null));
        var link = await Json(await recipient.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        // Moving or deleting a container must not bypass the protection of a shared root inside it.
        var protectedFile = Id(await Upload(owner, "protected.jpg", nested));
        await Json(await Share(owner, protectedFile, recipientId, "write"));
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{nested}/move", new { parentId = destination })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.DeleteAsync($"/elements/{nested}")).StatusCode);
        Assert.Equal(protectedFile, Id(Assert.Single((await Json(await owner.GetAsync($"/folders/{nested}/elements"))).EnumerateArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_write_replaces_only_its_content_even_with_a_hidden_or_read_only_parent(bool visibleParent)
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Private" })));
        var file = Id(await Upload(owner, "photo.jpg", folder));
        if (visibleParent) await Json(await Share(owner, folder, recipientId));
        await Json(await Share(owner, file, recipientId, "write"));
        byte[] bytes = "replacement with a different length"u8.ToArray();
        var pending = await Json(await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = file, size = bytes.Length, contentType = "text/plain" }));
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(recipient, pending)).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, bytes)).StatusCode);
        var updated = await Json(await Confirm(recipient, pending));
        Assert.Equal(file, Id(updated));
        Assert.Equal("photo.jpg", updated.GetProperty("name").GetString());
        Assert.Equal(folder, updated.GetProperty("parentId").GetGuid());
        Assert.Equal("text/plain", updated.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, updated.GetProperty("size").GetInt64());
        var link = await Json(await recipient.GetAsync($"/files/{file}/download"));
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.DeleteAsync($"/elements/{file}")).StatusCode);
        var old = Assert.Single((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        var restored = await Json(await owner.PostAsync($"/trash/{Id(old)}/restore", null));
        var oldLink = await Json(await owner.GetAsync($"/files/{Id(restored)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(oldLink.GetProperty("url").GetString()));
        Assert.Equal(visibleParent ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{Id(restored)}/download")).StatusCode);

        // Inherited write wins over direct read, including the level advertised in received shares.
        await Json(await Share(owner, folder, recipientId, "write"));
        await Json(await Share(owner, file, recipientId));
        Assert.Equal("write", (await Shared(recipient)).Single(e => Id(e.GetProperty("element")) == file).GetProperty("level").GetString());
        var inherited = await Upload(recipient, "photo.jpg", folder, onConflict: "replace");
        Assert.Equal(file, Id(inherited));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("revoked")]
    [InlineData("trashed")]
    public async Task Pending_additions_and_replacements_recheck_write_access_at_confirmation(string change)
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Shared" })));
        var file = Id(await Upload(owner, "original.jpg", folder));
        await Json(await Share(owner, folder, recipientId, "write"));
        await Json(await Share(owner, file, recipientId, "write"));
        var addition = await Declare(recipient, "new.jpg", Photo.Length, parentId: folder);
        var replacement = await Json(await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = file, size = 3 }));
        Assert.Equal(HttpStatusCode.OK, (await Put(addition, Photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(replacement, "new"u8.ToArray())).StatusCode);
        if (change == "trashed")
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{folder}")).StatusCode);
        else
            foreach (var id in new[] { folder, file })
                if (change == "read") await Json(await Share(owner, id, recipientId));
                else Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{id}/shares/{recipientId}")).StatusCode);

        var expected = change == "read" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
        Assert.Equal(expected, (await Confirm(recipient, addition)).StatusCode);
        Assert.Equal(expected, (await Confirm(recipient, replacement)).StatusCode);
        Assert.Equal(expected, (await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = file, size = 3 })).StatusCode);
        if (change == "trashed")
        {
            Assert.Empty(await Shared(recipient));
            Assert.Equal(folder, Id(Assert.Single((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray())));
            await Json(await owner.PostAsync($"/trash/{folder}/restore", null));
        }
        Assert.Empty((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal(file, Id(Assert.Single((await Json(await owner.GetAsync($"/folders/{folder}/elements"))).EnumerateArray())));
        var link = await Json(await owner.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Targeted_replacement_follows_file_identity_and_never_grants_access_to_its_parent_or_other_files()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Private" })));
        var file = Id(await Upload(owner, "old.jpg", folder));
        await Json(await Share(owner, file, recipientId, "write"));
        var pending = await Json(await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = file, size = 3 }));
        Assert.Equal(HttpStatusCode.OK, (await Put(pending, "new"u8.ToArray())).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/rename", new { name = "renamed.jpg" }));
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { }));
        var other = Id(await Upload(owner, "old.jpg", folder));
        var completed = await Json(await Confirm(recipient, pending));
        Assert.Equal(file, Id(completed));
        Assert.Equal("renamed.jpg", completed.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("parentId").ValueKind);
        var link = await Json(await recipient.GetAsync($"/files/{file}/download"));
        Assert.Equal("new", await Transfer.GetStringAsync(link.GetProperty("url").GetString()));
        var otherLink = await Json(await owner.GetAsync($"/files/{other}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(otherLink.GetProperty("url").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/folders/{folder}/elements")).StatusCode);
        foreach (var id in new[] { folder, other, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = id, size = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = file, size = 3, name = "injection.jpg" })).StatusCode);
    }

    [Fact]
    public async Task Owner_shares_a_file_for_reading_and_revokes_its_visibility()
    {
        var (ownerId, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var file = await Upload(owner, "photo.jpg");
        var id = Id(file);
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{id}/download")).StatusCode);
        Assert.Empty(await Shared(recipient));

        var grant = await Json(await Share(owner, id, recipientId));
        Assert.Equal("read", grant.GetProperty("level").GetString());
        var shared = Assert.Single(await Shared(recipient));
        Assert.Equal(id, Id(shared.GetProperty("element")));
        Assert.Equal(ownerId, Id(shared.GetProperty("owner")));
        Assert.Equal("alice", shared.GetProperty("owner").GetProperty("username").GetString());
        Assert.Empty(await Root(recipient));
        var download = await Json(await recipient.GetAsync($"/files/{id}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        Assert.Single((await Json(await owner.GetAsync($"/elements/{id}/shares"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{id}/shares/{recipientId}")).StatusCode);
        Assert.Empty(await Shared(recipient));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/files/{id}/download")).StatusCode);
    }
    [Fact]
    public async Task Folder_share_covers_present_and_future_descendants_without_exposing_ancestors()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var privateFolder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Private" })));
        var sharedFolder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photos", parentId = privateFolder })));
        var nested = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Summer", parentId = sharedFolder })));
        var oldFile = Id(await Upload(owner, "old.jpg", nested));
        await Json(await Share(owner, sharedFolder, recipientId));

        Assert.Equal(nested, Id(Assert.Single((await Json(await recipient.GetAsync($"/folders/{sharedFolder}/elements"))).EnumerateArray())));
        var newFile = Id(await Upload(owner, "new.jpg", nested));
        Assert.Equal(2, (await Json(await recipient.GetAsync($"/folders/{nested}/elements"))).GetArrayLength());
        foreach (var file in new[] { oldFile, newFile })
        {
            var link = await Json(await recipient.GetAsync($"/files/{file}/download"));
            Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
            var inherited = Assert.Single((await Json(await owner.GetAsync($"/elements/{file}/shares"))).EnumerateArray());
            Assert.Equal(sharedFolder, inherited.GetProperty("elementId").GetGuid());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/folders/{privateFolder}/elements")).StatusCode);
        Assert.Equal(sharedFolder, Id(Assert.Single(await Shared(recipient)).GetProperty("element")));
        Assert.Empty(await Root(recipient));

        // An overlapping file grant survives removal of the folder grant.
        await Json(await Share(owner, oldFile, recipientId));
        await owner.DeleteAsync($"/elements/{sharedFolder}/shares/{recipientId}");
        Assert.Equal(oldFile, Id(Assert.Single(await Shared(recipient)).GetProperty("element")));
        await Json(await recipient.GetAsync($"/files/{oldFile}/download"));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{newFile}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/folders/{nested}/elements")).StatusCode);
    }

    [Fact]
    public async Task Read_recipient_cannot_write_or_manage_shares_and_hidden_elements_stay_nonexistent()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var (outsiderId, outsider) = await User("charlie");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photos" })));
        var file = Id(await Upload(owner, "photo.jpg", folder));
        var ownFile = Id(await Upload(recipient, "own.jpg"));
        await Json(await Share(owner, folder, recipientId));

        foreach (var id in new[] { folder, file })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{id}/rename", new { name = "renamed" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{id}/move", new { space = "common" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.GetAsync($"/elements/{id}/shares")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Share(recipient, id, outsiderId)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await recipient.DeleteAsync($"/elements/{id}/shares/{recipientId}")).StatusCode);
            foreach (var thirdParty in new[] { outsider, Admin })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.PostAsJsonAsync($"/elements/{id}/rename", new { name = "renamed" })).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.PostAsJsonAsync($"/elements/{id}/move", new { space = "common" })).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.GetAsync($"/elements/{id}/shares")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await Share(thirdParty, id, outsiderId)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.DeleteAsync($"/elements/{id}/shares/{recipientId}")).StatusCode);
            }
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync("/folders", new { name = "Injected", parentId = folder })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync("/uploads", new { name = "injected.jpg", size = 1, parentId = folder })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await recipient.PostAsJsonAsync($"/elements/{ownFile}/move", new { parentId = folder })).StatusCode);
        foreach (var thirdParty in new[] { outsider, Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.GetAsync($"/folders/{folder}/elements")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.GetAsync($"/files/{file}/download")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.PostAsJsonAsync("/folders", new { name = "Injected", parentId = folder })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await thirdParty.PostAsJsonAsync("/uploads", new { name = "injected.jpg", size = 1, parentId = folder })).StatusCode);
        }
        Assert.Equal("Photos", Assert.Single(await Root(owner)).GetProperty("name").GetString());
        Assert.Equal("photo.jpg", Assert.Single((await Json(await owner.GetAsync($"/folders/{folder}/elements"))).EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(ownFile, Id(Assert.Single(await Root(recipient))));
    }

    [Fact]
    public async Task Owner_updates_grants_idempotently_and_invalid_or_unauthorized_requests_grant_no_access()
    {
        var (ownerId, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var (disabledId, _) = await User("disabled");
        await Admin.PostAsync($"/admin/accounts/{disabledId}/disable", null);
        var file = Id(await Upload(owner, "photo.jpg"));
        foreach (var invalidRecipient in new[] { ownerId, Guid.NewGuid(), disabledId })
        {
            var invalid = await Share(owner, file, invalidRecipient);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_recipient", await Error(invalid));
        }
        foreach (var level in new string?[] { null, "", "owner" })
        {
            var invalid = await owner.PutAsJsonAsync($"/elements/{file}/shares/{recipientId}", new { level });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_share_level", await Error(invalid));
        }
        Assert.Empty((await Json(await owner.GetAsync($"/elements/{file}/shares"))).EnumerateArray());
        Assert.Empty(await Shared(recipient));
        foreach (var attempt in Enumerable.Range(0, 3))
            await Json(await Share(owner, file, recipientId));
        Assert.Single((await Json(await owner.GetAsync($"/elements/{file}/shares"))).EnumerateArray());
        Assert.Single(await Shared(recipient));
        var invalidUpdate = await Share(owner, file, recipientId, "owner");
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
        Assert.Equal("read", Assert.Single(await Shared(recipient)).GetProperty("level").GetString());
        var common = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Common", space = "common" })));
        Assert.Equal(HttpStatusCode.Forbidden, (await Share(owner, common, recipientId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/elements/{common}/shares")).StatusCode);
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/elements/{missing}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Share(owner, missing, recipientId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/elements/{missing}/shares/{recipientId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/shares/received")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/elements/{file}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Share(Client, file, recipientId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.DeleteAsync($"/elements/{file}/shares/{recipientId}")).StatusCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task Moving_elements_recalculates_inheritance_and_common_transfer_removes_received_roots(string level)
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photos" })));
        var file = Id(await Upload(owner, "photo.jpg"));
        await Json(await Share(owner, folder, recipientId, level));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{file}/download")).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = folder }));
        await Json(await recipient.GetAsync($"/files/{file}/download"));
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{file}/download")).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = folder }));
        await Json(await Share(owner, file, recipientId, level));
        Assert.Equal(2, (await Shared(recipient)).Length);
        await Json(await owner.PostAsJsonAsync($"/elements/{folder}/move", new { space = "common" }));
        Assert.Empty(await Shared(recipient));
        // Common-space rights apply after transfer, including for an unshared administrator.
        await Json(await recipient.PostAsJsonAsync($"/elements/{file}/rename", new { name = "common.jpg" }));
        var download = await Json(await Admin.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

}
