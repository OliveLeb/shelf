using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class FolderTests : TestInstance
{
    private static async Task<JsonElement> CreateFolder(HttpClient client, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync("/folders", new { name, parentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }
    private static async Task<JsonElement[]> Content(HttpClient client, Guid folder, string query = "") =>
        [.. (await Json(await client.GetAsync($"/folders/{folder}/elements{query}"))).EnumerateArray()];
    private static string[] Names(JsonElement[] elements) => [.. elements.Select(e => e.GetProperty("name").GetString()!)];
    // Requests of one session serialize on its row; concurrent requests need their own sessions, like several devices.
    private async Task<HttpClient[]> Devices(int count) => await Task.WhenAll(Enumerable.Range(0, count).Select(_ => SignIn("member")));

    [Fact]
    public async Task Folders_nest_without_depth_limit_and_each_lists_only_its_own_content()
    {
        var (memberId, member) = await User();
        var folders = new List<JsonElement>();
        Guid? parent = null;
        for (var depth = 0; depth < 12; depth++)
        {
            var folder = await CreateFolder(member, "Photos", parent);
            Assert.Equal("folder", folder.GetProperty("kind").GetString());
            Assert.Equal(memberId, folder.GetProperty("addedBy").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, folder.GetProperty("size").ValueKind);
            folders.Add(folder);
            parent = Id(folder);
        }
        var deepest = Id(folders[^1]);
        // Names are unique per folder, so the same file name lives at the root and deep inside.
        await Upload(member, "photo.jpg");
        var file = await Upload(member, "photo.jpg", deepest);
        Assert.Equal("file", file.GetProperty("kind").GetString());
        Assert.Equal(deepest, file.GetProperty("parentId").GetGuid());

        Assert.Equal(["Photos", "photo.jpg"], Names(await Root(member)));
        for (var depth = 0; depth < folders.Count - 1; depth++)
            Assert.Equal(Id(folders[depth + 1]), Id(Assert.Single(await Content(member, Id(folders[depth])))));
        Assert.Equal(Id(file), Id(Assert.Single(await Content(member, deepest))));

        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Names_are_unique_per_folder_ignoring_case_for_files_and_folders()
    {
        var (_, member) = await User();
        var albums = Id(await CreateFolder(member, "Albums"));
        var notes = Id(await Upload(member, "notes.txt"));
        var other = Id(await Upload(member, "Other.txt", albums));

        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync("/folders", new { name = "ALBUMS" })));
        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync("/folders", new { name = "Notes.TXT" })));
        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync("/uploads", new { name = "albums", size = 1 })));
        Assert.Equal("name_conflict", await Error(await member.PostAsJsonAsync($"/elements/{notes}/rename", new { name = "albums" })));
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"/elements/{other}/rename", new { name = "NOTES.txt" })).StatusCode);
        var move = await member.PostAsJsonAsync($"/elements/{other}/move", new { parentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
        Assert.Equal("name_conflict", await Error(move));

        // The same names are free in another folder, and renaming an element to another case of its own name is allowed.
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/folders", new { name = "albums", parentId = albums })).StatusCode);
        var renamed = await member.PostAsJsonAsync($"/elements/{notes}/rename", new { name = "NOTES.TXT" });
        Assert.Equal("NOTES.TXT", (await Json(renamed)).GetProperty("name").GetString());
        Assert.Equal(["Albums", "NOTES.TXT"], Names(await Root(member)));
        Assert.Equal(["albums", "NOTES.txt"], Names(await Content(member, albums)));
    }

    [Fact]
    public async Task Keep_both_renames_an_upload_whose_name_is_taken_at_declaration_or_at_confirmation()
    {
        var (_, member) = await User();
        await Upload(member, "photo.jpg");
        Assert.Equal("PHOTO (1).jpg", (await Upload(member, "PHOTO.jpg", onConflict: "keepBoth")).GetProperty("name").GetString());
        Assert.Equal("photo (2).jpg", (await Upload(member, "photo.jpg", onConflict: "keepBoth")).GetProperty("name").GetString());
        await Upload(member, "README");
        Assert.Equal("README (1)", (await Upload(member, "README", onConflict: "keepBoth")).GetProperty("name").GetString());
        await Upload(member, ".profile");
        Assert.Equal(".profile (1)", (await Upload(member, ".profile", onConflict: "keepBoth")).GetProperty("name").GetString());
        var longName = new string('a', 250) + ".jpg";
        await Upload(member, longName);
        Assert.Equal(new string('a', 247) + " (1).jpg", (await Upload(member, longName, onConflict: "keepBoth")).GetProperty("name").GetString());

        // The name is free at declaration but taken before confirmation.
        var late = await Declare(member, "late.jpg", Photo.Length, onConflict: "keepBoth");
        Assert.Equal(HttpStatusCode.OK, (await Put(late, Photo)).StatusCode);
        await Upload(member, "late.jpg");
        Assert.Equal("late (1).jpg", (await Json(await Confirm(member, late))).GetProperty("name").GetString());

        Assert.Equal("invalid_conflict_policy", await Error(await member.PostAsJsonAsync("/uploads", new { name = "photo.jpg", size = 1, onConflict = "replace" })));
    }

    [Fact]
    public async Task Concurrent_uploads_of_the_same_name_never_create_a_duplicate()
    {
        var (_, member) = await User();
        var burst = Id(await CreateFolder(member, "Burst"));
        var devices = await Devices(6);
        async Task<HttpResponseMessage[]> ConfirmTogether(string? onConflict)
        {
            var uploads = new List<JsonElement>();
            for (var i = 0; i < 6; i++)
            {
                var upload = await Declare(member, "same.jpg", Photo.Length, parentId: burst, onConflict: onConflict);
                Assert.Equal(HttpStatusCode.OK, (await Put(upload, Photo)).StatusCode);
                uploads.Add(upload);
            }
            return await Task.WhenAll(uploads.Select((upload, i) => Confirm(devices[i], upload)));
        }

        var refused = await ConfirmTogether(null);
        Assert.Single(refused, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var conflict in refused.Where(r => r.StatusCode != HttpStatusCode.Created))
            Assert.Equal("name_conflict", await Error(conflict));
        Assert.Equal(["same.jpg"], Names(await Content(member, burst)));

        var kept = await ConfirmTogether("keepBoth");
        Assert.All(kept, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(["same (1).jpg", "same (2).jpg", "same (3).jpg", "same (4).jpg", "same (5).jpg", "same (6).jpg", "same.jpg"],
            Names(await Content(member, burst)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Moving_a_folder_into_itself_or_one_of_its_descendants_is_refused()
    {
        var (_, member) = await User();
        var a = Id(await CreateFolder(member, "A"));
        var b = Id(await CreateFolder(member, "B", a));
        var c = Id(await CreateFolder(member, "C", b));
        var file = Id(await Upload(member, "photo.jpg", c));
        foreach (var destination in new[] { a, b, c })
        {
            var move = await member.PostAsJsonAsync($"/elements/{a}/move", new { parentId = destination });
            Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
            Assert.Equal("folder_cycle", await Error(move));
        }
        Assert.Equal("folder_not_found", await Error(await member.PostAsJsonAsync($"/elements/{a}/move", new { parentId = file })));
        Assert.Equal("folder_not_found", await Error(await member.PostAsJsonAsync($"/elements/{a}/move", new { parentId = Guid.NewGuid() })));

        // Once C leaves A, A may go into C.
        Assert.Equal(JsonValueKind.Null, (await Json(await member.PostAsJsonAsync($"/elements/{c}/move", new { parentId = (Guid?)null }))).GetProperty("parentId").ValueKind);
        Assert.Equal(c, (await Json(await member.PostAsJsonAsync($"/elements/{a}/move", new { parentId = c }))).GetProperty("parentId").GetGuid());
        Assert.Equal(["C"], Names(await Root(member)));
        Assert.Equal(["A", "photo.jpg"], Names(await Content(member, c)));
        Assert.Equal(["B"], Names(await Content(member, a)));
    }

    [Fact]
    public async Task Two_concurrent_moves_never_form_a_cycle()
    {
        var (_, member) = await User();
        var devices = await Devices(2);
        const int rounds = 15;
        for (var round = 0; round < rounds; round++)
        {
            var x = Id(await CreateFolder(member, $"X{round}"));
            var y = Id(await CreateFolder(member, $"Y{round}"));
            var moves = await Task.WhenAll(
                devices[0].PostAsJsonAsync($"/elements/{x}/move", new { parentId = y }),
                devices[1].PostAsJsonAsync($"/elements/{y}/move", new { parentId = x }));
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], moves.Select(m => m.StatusCode).Order());
            Assert.Equal("folder_cycle", await Error(moves.Single(m => m.StatusCode == HttpStatusCode.Conflict)));
        }
        Assert.Equal(rounds, (await Root(member)).Length);
    }

    [Fact]
    public async Task Renaming_or_moving_does_not_touch_the_S3_object()
    {
        var (_, member) = await User();
        var file = Id(await Upload(member, "a.jpg"));
        async Task<string[]> Keys() => [.. ((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects ?? []).Select(o => $"{o.Key}:{o.ETag}:{o.LastModified:O}")];
        var before = await Keys();

        var folder = Id(await CreateFolder(member, "F"));
        Assert.Equal("b.jpg", (await Json(await member.PostAsJsonAsync($"/elements/{file}/rename", new { name = "b.jpg" }))).GetProperty("name").GetString());
        Assert.Equal(folder, (await Json(await member.PostAsJsonAsync($"/elements/{file}/move", new { parentId = folder }))).GetProperty("parentId").GetGuid());
        await Json(await member.PostAsJsonAsync($"/elements/{folder}/rename", new { name = "G" }));
        await Json(await member.PostAsJsonAsync($"/elements/{folder}/move", new { parentId = Id(await CreateFolder(member, "H")) }));

        Assert.Equal(before, await Keys());
        var download = await Json(await member.GetAsync($"/files/{file}/download"));
        var content = await Transfer.GetAsync(download.GetProperty("url").GetString());
        Assert.Equal(Photo, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("b.jpg", content.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task Folder_content_sorts_by_name_date_added_or_size_with_folders_first()
    {
        var (_, member) = await User();
        var folder = Id(await CreateFolder(member, "Sorted"));
        await CreateFolder(member, "z", folder);
        foreach (var (name, size) in new[] { ("B.txt", 30), ("a.txt", 10), ("c.txt", 20) })
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await Upload(member, name, folder, new byte[size]);
        }
        Clock.Advance(TimeSpan.FromMinutes(1));
        await CreateFolder(member, "Y", folder);

        Assert.Equal(["Y", "z", "a.txt", "B.txt", "c.txt"], Names(await Content(member, folder)));
        Assert.Equal(["z", "Y", "c.txt", "B.txt", "a.txt"], Names(await Content(member, folder, "?sort=name&order=desc")));
        Assert.Equal(["z", "Y", "B.txt", "a.txt", "c.txt"], Names(await Content(member, folder, "?sort=addedAt")));
        Assert.Equal(["Y", "z", "c.txt", "a.txt", "B.txt"], Names(await Content(member, folder, "?sort=addedAt&order=desc")));
        Assert.Equal(["Y", "z", "a.txt", "c.txt", "B.txt"], Names(await Content(member, folder, "?sort=size")));
        Assert.Equal(["Y", "z", "B.txt", "c.txt", "a.txt"], Names(await Content(member, folder, "?sort=size&order=desc")));
        Assert.Equal(["Sorted"], Names([.. (await Json(await member.GetAsync("/spaces/personal/elements?sort=size&order=desc"))).EnumerateArray()]));
        Assert.Equal("invalid_sort", await Error(await member.GetAsync($"/folders/{folder}/elements?sort=type")));
        Assert.Equal("invalid_order", await Error(await member.GetAsync($"/folders/{folder}/elements?order=up")));
    }

    [Fact]
    public async Task Folders_of_a_personal_space_are_invisible_to_other_users_and_administrators()
    {
        var (_, member) = await User();
        var (_, other) = await User("other");
        var folder = Id(await CreateFolder(member, "Private"));
        var file = Id(await Upload(member, "private.jpg", folder));

        foreach (var intruder in new[] { other, Admin })
        {
            var own = Id(await CreateFolder(intruder, "Mine"));
            Assert.Equal("folder_not_found", await Error(await intruder.GetAsync($"/folders/{folder}/elements")));
            Assert.Equal("folder_not_found", await Error(await intruder.PostAsJsonAsync("/folders", new { name = "Intrusion", parentId = folder })));
            Assert.Equal("folder_not_found", await Error(await intruder.PostAsJsonAsync("/uploads", new { name = "intrusion.jpg", size = 1, parentId = folder })));
            Assert.Equal("folder_not_found", await Error(await intruder.PostAsJsonAsync($"/elements/{own}/move", new { parentId = folder })));
            foreach (var element in new[] { folder, file })
            {
                var rename = await intruder.PostAsJsonAsync($"/elements/{element}/rename", new { name = "taken" });
                Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
                Assert.Equal("element_not_found", await Error(rename));
                Assert.Equal("element_not_found", await Error(await intruder.PostAsJsonAsync($"/elements/{element}/move", new { parentId = own })));
            }
            Assert.Equal(["Mine"], Names(await Root(intruder)));
        }
        Assert.Equal("element_not_found", await Error(await member.GetAsync($"/files/{folder}/download")));
        Assert.Equal("folder_not_found", await Error(await member.GetAsync($"/folders/{file}/elements")));
        Assert.Equal("invalid_name", await Error(await member.PostAsJsonAsync("/folders", new { name = "a/b" })));
        Assert.Equal("invalid_name", await Error(await member.PostAsJsonAsync($"/elements/{folder}/rename", new { name = ".." })));
        Assert.Equal(["Private"], Names(await Root(member)));
        Assert.Equal(["private.jpg"], Names(await Content(member, folder)));
    }
}
