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

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
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

        // The personal space, the only destination for now, disappears with its account.
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/admin/accounts/{memberId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(member, large)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(member, simple)).StatusCode);
        await Eventually(async () => (await PendingMultipartUploads()).Length == 0 && (await Objects()).Length == 0);
    }
}
