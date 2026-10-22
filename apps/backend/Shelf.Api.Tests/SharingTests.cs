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
        foreach (var level in new string?[] { null, "", "write", "owner" })
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
        var invalidUpdate = await Share(owner, file, recipientId, "write");
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

    [Fact]
    public async Task Moving_elements_recalculates_inheritance_and_common_transfer_removes_received_roots()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photos" })));
        var file = Id(await Upload(owner, "photo.jpg"));
        await Json(await Share(owner, folder, recipientId));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{file}/download")).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = folder }));
        await Json(await recipient.GetAsync($"/files/{file}/download"));
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{file}/download")).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/move", new { parentId = folder }));
        await Json(await Share(owner, file, recipientId));
        Assert.Equal(2, (await Shared(recipient)).Length);
        await Json(await owner.PostAsJsonAsync($"/elements/{folder}/move", new { space = "common" }));
        Assert.Empty(await Shared(recipient));
        // Common-space rights apply after transfer, including for an unshared administrator.
        await Json(await recipient.PostAsJsonAsync($"/elements/{file}/rename", new { name = "common.jpg" }));
        var download = await Json(await Admin.GetAsync($"/files/{file}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

}
