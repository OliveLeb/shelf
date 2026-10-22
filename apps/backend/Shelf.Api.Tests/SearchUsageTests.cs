using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SearchUsageTests : TestInstance
{
    private static async Task<Guid[]> Search(HttpClient client, string query) =>
        [.. (await Json(await client.GetAsync("/elements/search?query=" + Uri.EscapeDataString(query)))).EnumerateArray().Select(Id).Order()];

    [Fact]
    public async Task Search_matches_literal_substrings_across_visible_spaces_and_live_received_subtrees_only()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var personal = Id(await Upload(recipient, "My PHOTO.jpg"));
        var common = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo common", space = "common" })));
        var hidden = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo private" })));
        var shared = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo shared", parentId = hidden })));
        await Json(await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "read" }));
        var nested = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "nested", parentId = shared })));
        var file = Id(await Upload(owner, "a photo.jpg", nested));
        // Overlapping grants must not duplicate results, and a file grant does not expose its ancestors.
        await Json(await owner.PutAsJsonAsync($"/elements/{file}/shares/{recipientId}", new { level = "write" }));
        var direct = Id(await Upload(owner, "direct photo.jpg", hidden));
        await Json(await owner.PutAsJsonAsync($"/elements/{direct}/shares/{recipientId}", new { level = "read" }));
        Assert.Equal(new[] { personal, common, shared, file, direct }.Order(), await Search(recipient, "pHoTo"));
        Assert.Equal(new[] { common }, await Search(Admin, "photo"));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}/shares/{recipientId}")).StatusCode);
        Assert.Equal(new[] { personal, common, file, direct }.Order(), await Search(recipient, "photo"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}")).StatusCode);
        Assert.Equal(new[] { personal, common, direct }.Order(), await Search(recipient, "photo"));
        Assert.Equal(HttpStatusCode.NoContent, (await recipient.DeleteAsync($"/elements/{personal}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{common}")).StatusCode);
        Assert.Equal(new[] { direct }, await Search(recipient, "photo"));

        var literal = Id(await Upload(recipient, "100%_real.jpg"));
        await Upload(recipient, "100xxreal.jpg");
        Assert.Equal(new[] { literal }, await Search(recipient, "%_"));
        Assert.Empty(await Search(recipient, "' OR 1=1 --"));
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.GetAsync("/elements/search?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.GetAsync("/elements/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/elements/search?query=photo")).StatusCode);
    }

    [Fact]
    public async Task Usage_counts_confirmed_bytes_by_space_owner_including_trash_but_not_received_shares_or_pending_uploads()
    {
        var (aliceId, alice) = await User("alice");
        var (bobId, bob) = await User("bob");
        var (_, empty) = await User("empty");
        var initial = await Json(await alice.GetAsync("/storage/usage"));
        Assert.Equal(0, initial.GetProperty("personalBytes").GetInt64());
        Assert.Equal(0, initial.GetProperty("commonBytes").GetInt64());
        await Upload(alice, "private.txt", content: new byte[3]);
        var folder = Id(await Json(await alice.PostAsJsonAsync("/folders", new { name = "private folder" })));
        await Upload(alice, "nested.txt", folder, new byte[7]);
        var shared = Id(await Json(await bob.PostAsJsonAsync("/folders", new { name = "shared folder" })));
        await Upload(bob, "bob.txt", shared, new byte[11]);
        await Json(await bob.PutAsJsonAsync($"/elements/{shared}/shares/{aliceId}", new { level = "write" }));
        await Upload(alice, "added by alice.txt", shared, new byte[2]);
        var commonTicket = await Json(await alice.PostAsJsonAsync("/uploads", new { name = "common.txt", size = 13, space = "common" }));
        await Put(commonTicket, new byte[13]);
        var common = Id(await Json(await Confirm(alice, commonTicket)));
        var pending = await Declare(alice, "pending.txt", 17);
        await Put(pending, new byte[17]);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/elements/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"/elements/{common}")).StatusCode);

        var usage = await Json(await alice.GetAsync("/storage/usage"));
        Assert.Equal(new[] { "commonBytes", "personalBytes" }, usage.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(10, usage.GetProperty("personalBytes").GetInt64());
        Assert.Equal(13, usage.GetProperty("commonBytes").GetInt64());
        Assert.Equal(13, (await Json(await bob.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        Assert.Equal(0, (await Json(await empty.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        // Account administration sees only identity and a total, never file/folder names or metadata.
        var totals = (await Json(await Admin.GetAsync("/admin/storage/usage"))).EnumerateArray().ToArray();
        Assert.Equal(4, totals.Length);
        foreach (var total in totals)
        {
            Assert.Equal(new[] { "account", "bytes" }, total.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(new[] { "id", "username" }, total.GetProperty("account").EnumerateObject().Select(p => p.Name).Order());
        }
        Assert.Equal(10, totals.Single(t => Id(t.GetProperty("account")) == aliceId).GetProperty("bytes").GetInt64());
        Assert.Equal(13, totals.Single(t => Id(t.GetProperty("account")) == bobId).GetProperty("bytes").GetInt64());
        Assert.Equal(2, totals.Count(t => t.GetProperty("bytes").GetInt64() == 0));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/admin/storage/usage")).StatusCode);
        foreach (var route in new[] { "/storage/usage", "/admin/storage/usage" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(route)).StatusCode);

        await Json(await alice.PostAsync($"/trash/{folder}/restore", null));
        Assert.Equal(10, (await Json(await alice.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/elements/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/trash/{folder}")).StatusCode);
        Assert.Equal(3, (await Json(await alice.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        // Replacement retains the old bytes in trash, and consulting usage imposes no quota on subsequent writes.
        await Upload(alice, "private.txt", content: new byte[5], onConflict: "replace");
        Assert.Equal(8, (await Json(await alice.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/spaces/common/trash")).StatusCode);
        Assert.Equal(0, (await Json(await bob.GetAsync("/storage/usage"))).GetProperty("commonBytes").GetInt64());
    }
}
