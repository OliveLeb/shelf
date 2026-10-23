using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class MultipartUploadTests : TestInstance
{
    private const int MiB = 1024 * 1024;
    // Three parts with the 5 MiB test part size: two full parts and a shorter last one.
    private static readonly byte[] Video = Random.Shared.GetItems(Enumerable.Range(0, 256).Select(b => (byte)b).ToArray(), 11 * MiB);

    private static int[] Numbers(JsonElement upload) => [.. upload.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("number").GetInt32())];
    private static Task<HttpResponseMessage> Resume(HttpClient client, JsonElement upload) => client.GetAsync($"/uploads/{upload.GetProperty("id").GetGuid()}");
    private async Task<string[]> PendingMultipartUploads() => [.. ((await S3.ListMultipartUploadsAsync(Bucket)).MultipartUploads ?? []).Select(u => u.Key)];
    private async Task<string[]> Objects() => [.. ((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects ?? []).Select(o => o.Key)];
    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(15); !await condition(); await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time");
    }

    [Fact]
    public async Task Large_file_is_sent_in_parts_and_appears_once_confirmation_assembles_it()
    {
        var (_, member) = await User();
        var upload = await Declare(member, "holiday.mp4", Video.Length, "video/mp4");
        Assert.Equal(5 * MiB, upload.GetProperty("partSize").GetInt64());
        Assert.Equal([1, 2, 3], Numbers(upload));
        Assert.Empty(upload.GetProperty("headers").EnumerateObject());

        await Put(upload, Video);
        Assert.Empty(await Root(member));

        var confirmation = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        var file = await Json(confirmation);
        Assert.Equal(Video.Length, file.GetProperty("size").GetInt64());
        Assert.Equal("video/mp4", file.GetProperty("contentType").GetString());
        Assert.Equal("holiday.mp4", Assert.Single(await Root(member)).GetProperty("name").GetString());
        Assert.Empty(await PendingMultipartUploads());

        var download = await Json(await member.GetAsync($"/files/{file.GetProperty("id").GetGuid()}/download"));
        var content = await Transfer.GetAsync(download.GetProperty("url").GetString());
        Assert.Equal(Video, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("video/mp4", content.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Interrupted_upload_resumes_with_the_missing_parts_only()
    {
        var (_, member) = await User();
        var upload = await Declare(member, "holiday.mp4", Video.Length, "video/mp4");
        // The network drops after parts 1 and 3; part 2 arrives truncated.
        await Put(upload, Video, 1, 3);
        var incomplete = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        Assert.Equal("upload_incomplete", await Error(incomplete));
        await Put(upload, Video[..(7 * MiB)], 2);
        var truncated = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Conflict, truncated.StatusCode);
        Assert.Equal("size_mismatch", await Error(truncated));
        Assert.Empty(await Root(member));

        var resumed = await Json(await Resume(member, upload));
        Assert.Equal(upload.GetProperty("id").GetGuid(), resumed.GetProperty("id").GetGuid());
        Assert.Equal([2], Numbers(resumed));
        await Put(resumed, Video);
        Assert.Empty(Numbers(await Json(await Resume(member, upload))));

        Assert.Equal(HttpStatusCode.Created, (await Confirm(member, upload)).StatusCode);
        var file = Assert.Single(await Root(member));
        var download = await Json(await member.GetAsync($"/files/{file.GetProperty("id").GetGuid()}/download"));
        Assert.Equal(Video, await (await Transfer.GetAsync(download.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Resume(member, upload)).StatusCode);
    }

    [Fact]
    public async Task Simple_upload_resumes_with_its_single_part_until_the_object_is_sent()
    {
        var (_, member) = await User();
        var photo = Video[..1000];
        var upload = await Declare(member, "photo.jpg", photo.Length);
        Assert.Equal(1000, upload.GetProperty("partSize").GetInt64());
        Assert.Equal(new[] { 1 }, Numbers(await Json(await Resume(member, upload))));
        await Put(upload, photo);
        Assert.Empty(Numbers(await Json(await Resume(member, upload))));
        Assert.Equal(HttpStatusCode.Created, (await Confirm(member, upload)).StatusCode);
    }

    [Fact]
    public async Task Upload_declaration_has_no_application_size_limit()
    {
        var (_, member) = await User();
        const long fiveTebibytes = 5L * 1024 * 1024 * MiB; // The largest object S3 accepts.
        var upload = await Declare(member, "archive.tar", fiveTebibytes, "application/x-tar");
        var partSize = upload.GetProperty("partSize").GetInt64();
        // S3 accepts at most 10,000 parts, so the part size grows with the file.
        Assert.InRange((fiveTebibytes + partSize - 1) / partSize, 9_000, 10_000);
        Assert.Equal(0, partSize % MiB);
        // URLs come in batches: the client asks for the next missing parts after sending these.
        Assert.Equal(Enumerable.Range(1, 1000), Numbers(upload));
    }

    [Fact]
    public async Task Unconfirmed_uploads_expire_and_their_parts_and_objects_are_purged()
    {
        var (_, member) = await User();
        var kept = await Declare(member, "kept.jpg", 10);
        await Put(kept, Video[..10]);
        Assert.Equal(HttpStatusCode.Created, (await Confirm(member, kept)).StatusCode);
        await Eventually(async () => (await Objects()).Length == 1); // Confirmed upload's temporary key is deleted asynchronously.
        var keptObjects = await Objects();
        var large = await Declare(member, "holiday.mp4", Video.Length, "video/mp4");
        await Put(large, Video, 1);
        var simple = await Declare(member, "photo.jpg", 10);
        await Put(simple, Video[..10]);
        Assert.Single(await PendingMultipartUploads());
        Assert.Equal(2, (await Objects()).Length);

        Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(5));
        await Task.Delay(500); // Several cleanup rounds run before the expiry delay.
        var resumed = await Json(await Resume(member, large));
        Assert.Equal([2, 3], Numbers(resumed));
        // Signed URLs never outlive the upload, so nothing reaches the bucket after its purge.
        Assert.InRange(resumed.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Single(await PendingMultipartUploads());
        Assert.Equal(2, (await Objects()).Length);

        Clock.Advance(TimeSpan.FromMinutes(5));
        foreach (var upload in new[] { large, simple })
        {
            var resume = await Resume(member, upload);
            Assert.Equal(HttpStatusCode.NotFound, resume.StatusCode);
            Assert.Equal("upload_not_found", await Error(resume));
            Assert.Equal(HttpStatusCode.NotFound, (await Confirm(member, upload)).StatusCode);
        }
        await Eventually(async () => (await PendingMultipartUploads()).Length == 0 && (await Objects()).SequenceEqual(keptObjects));
        Assert.Equal("kept.jpg", Assert.Single(await Root(member)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Confirmation_after_the_destination_disappeared_fails_and_the_objects_are_purged()
    {
        var (memberId, member) = await User();
        var large = await Declare(member, "holiday.mp4", Video.Length, "video/mp4");
        await Put(large, Video);
        var simple = await Declare(member, "photo.jpg", 10);
        await Put(simple, Video[..10]);

        // Deleting the account also deletes its personal space.
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{memberId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(member, large)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(member, simple)).StatusCode);
        // Purged at expiry, once no signed URL can recreate the object.
        await Task.Delay(500);
        Assert.Single(await PendingMultipartUploads());
        Assert.Single(await Objects());
        Clock.Advance(TimeSpan.FromHours(24));
        await Eventually(async () => (await PendingMultipartUploads()).Length == 0 && (await Objects()).Length == 0);
    }

    [Fact]
    public async Task Multipart_upload_resumes_in_its_folder_and_keeps_both_names_on_confirmation()
    {
        var (_, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Videos" })));
        await Upload(member, "holiday.mp4", folder);
        var upload = await Declare(member, "holiday.mp4", Video.Length, "video/mp4", folder, "keepBoth");
        await Put(upload, Video, 1, 3);
        var resumed = await Json(await Resume(member, upload));
        Assert.Equal([2], Numbers(resumed));
        await Put(resumed, Video);

        var file = await Json(await Confirm(member, upload));
        Assert.Equal("holiday (1).mp4", file.GetProperty("name").GetString());
        Assert.Equal(folder, file.GetProperty("parentId").GetGuid());
        var children = (await Json(await member.GetAsync($"/folders/{folder}/elements"))).EnumerateArray().ToArray();
        Assert.Equal(2, children.Length);
        Assert.Equal("folder", Assert.Single(await Root(member)).GetProperty("kind").GetString());
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal(Video, await (await Transfer.GetAsync(download.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Deleted_folder_prevents_confirmation_and_its_pending_objects_are_purged_at_expiry()
    {
        var (_, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Videos" })));
        var large = await Declare(member, "holiday.mp4", Video.Length, "video/mp4", folder);
        await Put(large, Video, 1);
        var simple = await Declare(member, "photo.jpg", 10, parentId: folder);
        await Put(simple, Video[..10]);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/elements/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/trash/{folder}")).StatusCode);

        Assert.Empty(await Root(member)); // The account and its session remain active.
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/folders/{folder}/elements")).StatusCode);
        foreach (var upload in new[] { large, simple })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Resume(member, upload)).StatusCode);
            var confirmation = await Confirm(member, upload);
            Assert.Equal(HttpStatusCode.NotFound, confirmation.StatusCode);
            Assert.Equal("upload_not_found", await Error(confirmation));
        }
        await Task.Delay(500);
        Assert.Single(await PendingMultipartUploads());
        Assert.Single(await Objects());
        Clock.Advance(TimeSpan.FromHours(24));
        await Eventually(async () => (await PendingMultipartUploads()).Length == 0 && (await Objects()).Length == 0);
        Assert.Empty(await Root(member));
    }

    [Theory]
    [InlineData(false, "read")]
    [InlineData(false, "revoked")]
    [InlineData(true, "read")]
    [InlineData(true, "revoked")]
    public async Task Multipart_additions_and_replacements_recheck_write_access_and_expire_without_deleting_confirmed_content(bool replacement, string change)
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Shared" })));
        var original = await Upload(owner, "original.jpg", folder);
        var shared = replacement ? Id(original) : folder;
        await Json(await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "write" }));
        var upload = replacement
            ? await Json(await recipient.PostAsJsonAsync("/uploads", new { replaceFileId = shared, size = Video.Length, contentType = "video/mp4" }))
            : await Declare(recipient, "pending.mp4", Video.Length, "video/mp4", folder);
        await Put(upload, Video, 1, 3);
        var resumed = await Json(await Resume(recipient, upload));
        Assert.Equal([2], Numbers(resumed));
        await Put(resumed, Video);
        // A separate confirmed multipart file must survive both cleanup workers.
        var kept = await Upload(owner, "kept.mp4", content: Video);

        if (change == "read")
            await Json(await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "read" }));
        else
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}/shares/{recipientId}")).StatusCode);
        var expected = change == "read" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
        Assert.Equal(expected, (await Resume(recipient, upload)).StatusCode);
        Assert.Equal(expected, (await Confirm(recipient, upload)).StatusCode);
        Assert.Equal(Id(original), Id(Assert.Single((await Json(await owner.GetAsync($"/folders/{folder}/elements"))).EnumerateArray())));

        Clock.Advance(TimeSpan.FromHours(24));
        await Eventually(async () => (await PendingMultipartUploads()).Length == 0 && (await Objects()).Length == 2);
        foreach (var (file, bytes) in new[] { (original, Photo), (kept, Video) })
        {
            var download = await Json(await owner.GetAsync($"/files/{Id(file)}/download"));
            Assert.Equal(bytes, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        }
    }

}
