using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class ThumbnailTests : TestInstance
{
    private static Task<byte[]> Fixture(string name) => File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static async Task<JsonElement> Preview(HttpClient client, Guid id) =>
        await Json(await client.GetAsync($"/files/{id}/thumbnail"));

    private static async Task<JsonElement> Ready(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement result;
        do
        {
            result = await Preview(client, id);
            if (result.GetProperty("state").GetString() != "pending") return result;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail("Miniature remained pending after 15 seconds.");
        return result;
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg", "320x213")]
    [InlineData("photo.heic", "image/heic", "320x213")]
    [InlineData("video.mp4", "video/mp4", "320x180")]
    public async Task Confirmed_file_gets_a_distinct_JPEG_miniature_without_changing_downloaded_bytes(string name, string type, string dimensions)
    {
        var (_, owner) = await User();
        var bytes = await Fixture(name);
        var ticket = await Declare(owner, name, bytes.Length, type);
        await Put(ticket, bytes);
        var file = await Json(await Confirm(owner, ticket));
        Assert.Equal("pending", file.GetProperty("thumbnailState").GetString());

        var preview = await Ready(owner, Id(file));
        Assert.Equal("available", preview.GetProperty("state").GetString());
        var link = preview.GetProperty("link");
        using var image = await Transfer.GetAsync(link.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        var miniature = await image.Content.ReadAsByteArrayAsync();
        Assert.True(miniature.Length > 4);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, miniature[..2]);
        // Decode the delivered JPEG independently, rather than trusting its MIME type or magic bytes.
        var start = new System.Diagnostics.ProcessStartInfo("identify") { RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-format", "%wx%h", "jpeg:-" }) start.ArgumentList.Add(arg);
        using var identify = System.Diagnostics.Process.Start(start)!;
        await identify.StandardInput.BaseStream.WriteAsync(miniature);
        identify.StandardInput.Close();
        var actualDimensions = await identify.StandardOutput.ReadToEndAsync();
        await identify.WaitForExitAsync();
        Assert.Equal(0, identify.ExitCode);
        Assert.Equal(dimensions, actualDimensions);
        var download = await Json(await owner.GetAsync($"/files/{Id(file)}/download"));
        Assert.NotEqual(new Uri(download.GetProperty("url").GetString()!).AbsolutePath, new Uri(link.GetProperty("url").GetString()!).AbsolutePath);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        Assert.Equal("available", Assert.Single(await Root(owner)).GetProperty("thumbnailState").GetString());
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/jpeg")]
    [InlineData("video/mp4")]
    public async Task Unsupported_or_corrupt_content_becomes_unavailable_and_is_not_retried_after_restart(string type)
    {
        var (_, owner) = await User();
        var ticket = await Declare(owner, "broken", Photo.Length, type);
        await Put(ticket, Photo);
        var id = Id(await Json(await Confirm(owner, ticket)));
        var preview = await Ready(owner, id);
        Assert.Equal("unavailable", preview.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("link").ValueKind);
        await RestartApp();
        Clock.Advance(TimeSpan.FromHours(1));
        using var signedIn = await SignIn("member");
        await Task.Delay(1200);
        Assert.Equal("unavailable", (await Preview(signedIn, id)).GetProperty("state").GetString());
        var link = await Json(await signedIn.GetAsync($"/files/{id}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        Assert.Single((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects);
    }

    [Fact]
    public async Task Video_stream_uses_original_bytes_with_partial_requests_and_the_same_read_access_as_miniatures()
    {
        var (_, owner) = await User();
        var (readerId, reader) = await User("reader");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Private" })));
        var bytes = await Fixture("video.mp4");
        var ticket = await Declare(owner, "video.mp4", bytes.Length, "video/mp4", folder);
        await Put(ticket, bytes);
        var id = Id(await Json(await Confirm(owner, ticket)));
        Assert.Equal("available", (await Ready(owner, id)).GetProperty("state").GetString());
        foreach (var route in new[] { "thumbnail", "stream" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/files/{id}/{route}")).StatusCode);
            foreach (var intruder in new[] { reader, Admin })
                Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/files/{id}/{route}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/files/{folder}/{route}")).StatusCode);
        }
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{readerId}", new { level = "read" }));
        Assert.Equal("available", (await Preview(reader, id)).GetProperty("state").GetString());
        var stream = await Json(await reader.GetAsync($"/files/{id}/stream"));
        var url = stream.GetProperty("url").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(37, 188);
        using var part = await Transfer.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal($"bytes 37-188/{bytes.Length}", part.Content.Headers.ContentRange?.ToString());
        Assert.Equal(bytes[37..189], await part.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline", part.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("video/mp4", part.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(url));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{folder}/shares/{readerId}")).StatusCode);
        foreach (var route in new[] { "thumbnail", "stream" })
            Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/files/{id}/{route}")).StatusCode);

        // A common file needs no share; the original stays private after only the copy moves there.
        var copy = Id(await Json(await owner.PostAsJsonAsync($"/elements/{id}/copy", new { space = "common" })));
        Assert.Equal("available", (await Ready(reader, copy)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/files/{copy}/stream")).StatusCode);
        var logs = string.Join('\n', Logs.Messages);
        Assert.DoesNotContain("X-Amz-Signature", logs);
        Assert.DoesNotContain(S3SecretKey, logs);
    }

    [Fact]
    public async Task Replacement_and_copy_keep_independent_miniatures_and_purge_removes_only_the_target_objects()
    {
        var (_, owner) = await User();
        var bytes = await Fixture("photo.heic");
        var ticket = await Declare(owner, "photo.heic", bytes.Length, "image/heic");
        await Put(ticket, bytes);
        var id = Id(await Json(await Confirm(owner, ticket)));
        var old = (await Ready(owner, id)).GetProperty("link").GetProperty("url").GetString();
        var copy = Id(await Json(await owner.PostAsJsonAsync($"/elements/{id}/copy", new { })));
        var copied = (await Ready(owner, copy)).GetProperty("link").GetProperty("url").GetString();
        Assert.NotEqual(new Uri(old!).AbsolutePath, new Uri(copied!).AbsolutePath);

        var replacement = await Declare(owner, "photo.heic", Photo.Length, "application/octet-stream", onConflict: "replace");
        await Put(replacement, Photo);
        Assert.Equal(id, Id(await Json(await Confirm(owner, replacement))));
        Assert.Equal("unavailable", (await Ready(owner, id)).GetProperty("state").GetString());
        var trash = Assert.Single((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal("available", trash.GetProperty("element").GetProperty("thumbnailState").GetString());
        await Json(await owner.PostAsync($"/trash/{Id(trash)}/restore", null));
        var restoredUrl = (await Preview(owner, Id(trash))).GetProperty("link").GetProperty("url").GetString();
        Assert.Equal(new Uri(old!).AbsolutePath, new Uri(restoredUrl!).AbsolutePath);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{Id(trash)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/files/{Id(trash)}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/trash/{Id(trash)}")).StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await Transfer.GetAsync(old)).StatusCode != HttpStatusCode.NotFound) await Task.Delay(100);
        Assert.Equal(HttpStatusCode.NotFound, (await Transfer.GetAsync(old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Transfer.GetAsync(copied)).StatusCode);
        // Reconciliation must not mistake a live miniature for an orphan.
        Clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(2200);
        Assert.Equal(HttpStatusCode.OK, (await Transfer.GetAsync(copied)).StatusCode);
        Assert.Equal(3, (await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects.Count);
    }
}
