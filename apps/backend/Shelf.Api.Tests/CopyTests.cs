using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class CopyTests : TestInstance
{
    [Fact]
    public async Task Read_recipient_copies_a_folder_recursively_into_their_own_space_without_shares()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var (thirdId, third) = await User("charlie");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photos" })));
        var nested = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Summer", parentId = folder })));
        var file = Id(await Upload(owner, "sun.jpg", nested));
        await Json(await owner.PostAsJsonAsync("/folders", new { name = "Empty", parentId = folder }));
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "read" }));
        await Json(await owner.PutAsJsonAsync($"/elements/{file}/shares/{thirdId}", new { level = "read" }));

        var response = await recipient.PostAsJsonAsync($"/elements/{folder}/copy", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = await Json(response);
        Assert.NotEqual(folder, Id(copy));
        Assert.Equal("Photos", copy.GetProperty("name").GetString());
        Assert.Equal(recipientId, copy.GetProperty("addedBy").GetProperty("id").GetGuid());
        Assert.Equal(Id(copy), Id(Assert.Single(await Root(recipient))));
        var children = (await Json(await recipient.GetAsync($"/folders/{Id(copy)}/elements"))).EnumerateArray().ToArray();
        Assert.Equal(new[] { "Empty", "Summer" }, children.Select(e => e.GetProperty("name").GetString()));
        var empty = children[0];
        Assert.Empty((await Json(await recipient.GetAsync($"/folders/{Id(empty)}/elements"))).EnumerateArray());
        var copiedFile = Assert.Single((await Json(await recipient.GetAsync($"/folders/{Id(children[1])}/elements"))).EnumerateArray());
        Assert.NotEqual(file, Id(copiedFile));
        Assert.Equal(Id(children[1]), copiedFile.GetProperty("parentId").GetGuid());
        Assert.Equal("sun.jpg", copiedFile.GetProperty("name").GetString());
        Assert.Equal(Photo.Length, copiedFile.GetProperty("size").GetInt64());
        Assert.Equal("image/jpeg", copiedFile.GetProperty("contentType").GetString());
        var link = await Json(await recipient.GetAsync($"/files/{Id(copiedFile)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        foreach (var element in new[] { copy, children[0], children[1], copiedFile })
        {
            Assert.Empty((await Json(await recipient.GetAsync($"/elements/{Id(element)}/shares"))).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/elements/{Id(element)}/copy", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await third.PostAsJsonAsync($"/elements/{Id(element)}/copy", new { })).StatusCode);
        }
        // The recipient is the new owner and may rename, independently of the original and its grants.
        await Json(await recipient.PostAsJsonAsync($"/elements/{Id(copiedFile)}/rename", new { name = "mine.jpg" }));
        Assert.Equal("sun.jpg", Assert.Single((await Json(await owner.GetAsync($"/folders/{nested}/elements"))).EnumerateArray()).GetProperty("name").GetString());
        await owner.DeleteAsync($"/elements/{folder}/shares/{recipientId}");
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{file}/download")).StatusCode);
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5 * 1024 * 1024, false)]
    [InlineData(5 * 1024 * 1024 + 17, true)]
    [InlineData(10 * 1024 * 1024 + 71, true)]
    public async Task Objects_copy_independently_using_multipart_only_above_the_provider_limit(int size, bool multipart)
    {
        var (_, member) = await User();
        // A real provider limit, lowered to exercise both sides without transferring multiple GiB.
        await using var copyApp = App.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:SingleCopyLimitBytes"] = "5242880",
                ["Storage:S3:CopyPartSizeBytes"] = "5242880"
            })));
        using var copier = copyApp.CreateClient();
        copier.DefaultRequestHeaders.Authorization = member.DefaultRequestHeaders.Authorization;
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i / 1024 + i % 251) % 256);
        var original = Id(await Upload(member, "video.mp4", content: bytes));
        var originalLink = await Json(await member.GetAsync($"/files/{original}/download"));
        var originalUrl = originalLink.GetProperty("url").GetString()!;
        var copy = await Json(await copier.PostAsJsonAsync($"/elements/{original}/copy", new { }));
        Assert.Equal("video (1).mp4", copy.GetProperty("name").GetString());
        var copyLink = await Json(await member.GetAsync($"/files/{Id(copy)}/download"));
        var copyUrl = copyLink.GetProperty("url").GetString()!;
        var sourceKey = new Uri(originalUrl).AbsolutePath.Split('/', 3)[2];
        var copyKey = new Uri(copyUrl).AbsolutePath.Split('/', 3)[2];
        Assert.NotEqual(sourceKey, copyKey);
        var metadata = await S3.GetObjectMetadataAsync(Bucket, copyKey);
        Assert.Equal(multipart, metadata.ETag.Contains('-'));
        if (size > 10 * 1024 * 1024) Assert.EndsWith("-3\"", metadata.ETag);
        Assert.Equal(size, metadata.ContentLength);
        Assert.Equal("image/jpeg", metadata.Headers.ContentType);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(copyUrl));

        await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = copyKey, ContentBody = "changed copy" });
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(originalUrl));
        await S3.DeleteObjectAsync(Bucket, copyKey);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(originalUrl));
        var another = await Json(await copier.PostAsJsonAsync($"/elements/{original}/copy", new { }));
        var anotherLink = await Json(await member.GetAsync($"/files/{Id(another)}/download"));
        await S3.DeleteObjectAsync(Bucket, sourceKey);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(anotherLink.GetProperty("url").GetString()));
        Assert.Empty((await S3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = Bucket })).MultipartUploads ?? []);
    }

    [Fact]
    public async Task Files_copy_from_personal_shared_and_common_sources_into_writable_destinations()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var file = Id(await Upload(owner, "photo.jpg"));
        await Json(await owner.PutAsJsonAsync($"/elements/{file}/shares/{recipientId}", new { level = "read" }));
        var personalFolder = Id(await Json(await recipient.PostAsJsonAsync("/folders", new { name = "Mine" })));
        var commonFolder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Everyone", space = "common" })));
        var inPersonal = await Json(await recipient.PostAsJsonAsync($"/elements/{file}/copy", new { parentId = personalFolder }));
        var inCommon = await Json(await recipient.PostAsJsonAsync($"/elements/{file}/copy", new { space = "common" }));
        var inCommonFolder = await Json(await recipient.PostAsJsonAsync($"/elements/{file}/copy", new { parentId = commonFolder }));
        Assert.Equal(Id(inPersonal), Id(Assert.Single((await Json(await recipient.GetAsync($"/folders/{personalFolder}/elements"))).EnumerateArray())));
        Assert.Equal(Id(inCommonFolder), Id(Assert.Single((await Json(await Admin.GetAsync($"/folders/{commonFolder}/elements"))).EnumerateArray())));
        foreach (var common in new[] { inCommon, inCommonFolder })
        {
            var link = await Json(await Admin.GetAsync($"/files/{Id(common)}/download"));
            Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        }
        var fromCommon = await Json(await recipient.PostAsJsonAsync($"/elements/{Id(inCommon)}/copy", new { }));
        Assert.Equal(Id(fromCommon), Id(Assert.Single(await Root(recipient), e => e.GetProperty("kind").GetString() == "file")));
        Assert.Empty((await Json(await recipient.GetAsync($"/elements/{Id(fromCommon)}/shares"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/files/{Id(fromCommon)}/download")).StatusCode);
        await Json(await owner.PostAsJsonAsync($"/elements/{file}/copy", new { space = "common" }));
        Assert.Equal(2, (await Json(await owner.GetAsync("/spaces/common/elements"))).EnumerateArray().Count(e => e.GetProperty("kind").GetString() == "file"));
    }

    [Fact]
    public async Task Invisible_sources_and_destinations_return_404_and_read_only_destinations_return_403()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var privateFolder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Private" })));
        var readOnlyFolder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Read only" })));
        var file = Id(await Upload(owner, "private.jpg", privateFolder));
        var ownFile = Id(await Upload(recipient, "own.jpg"));
        await Json(await owner.PutAsJsonAsync($"/elements/{readOnlyFolder}/shares/{recipientId}", new { level = "read" }));
        foreach (var actor in new[] { recipient, Admin })
        {
            foreach (var source in new[] { privateFolder, file, Guid.NewGuid() })
            {
                var response = await actor.PostAsJsonAsync($"/elements/{source}/copy", new { space = "common" });
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("element_not_found", await Error(response));
            }
        }
        foreach (var parent in new[] { privateFolder, Guid.NewGuid(), ownFile })
        {
            var response = await recipient.PostAsJsonAsync($"/elements/{ownFile}/copy", new { parentId = parent });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("folder_not_found", await Error(response));
        }
        var forbidden = await recipient.PostAsJsonAsync($"/elements/{ownFile}/copy", new { parentId = readOnlyFolder });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("write_forbidden", await Error(forbidden));
        var mismatch = await recipient.PostAsJsonAsync($"/elements/{ownFile}/copy", new { parentId = readOnlyFolder, space = "common" });
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.PostAsJsonAsync($"/elements/{ownFile}/copy", new { space = "unknown" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync($"/elements/{file}/copy", new { })).StatusCode);
        Assert.Equal(ownFile, Id(Assert.Single(await Root(recipient))));
        Assert.Empty((await Json(await recipient.GetAsync("/spaces/common/elements"))).EnumerateArray());
    }

    [Fact]
    public async Task Concurrent_copies_resolve_case_insensitive_conflicts_without_replacing_any_element()
    {
        var (_, member) = await User();
        var source = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Source" })));
        var file = Id(await Upload(member, "photo.jpg", source));
        await Json(await member.PostAsJsonAsync("/folders", new { name = "PHOTO.JPG" }));
        await Upload(member, "photo (1).jpg", content: "do not replace"u8.ToArray());
        // Distinct sessions avoid serializing requests on a session's last_seen_at update.
        var devices = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => SignIn("member", device: $"copier-{i}")));
        HttpResponseMessage[] responses;
        try { responses = await Task.WhenAll(devices.Select(device => device.PostAsJsonAsync($"/elements/{file}/copy", new { }))); }
        finally { foreach (var device in devices) device.Dispose(); }
        var copies = await Task.WhenAll(responses.Select(Json));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(new[] { "photo (2).jpg", "photo (3).jpg", "photo (4).jpg", "photo (5).jpg" },
            copies.Select(e => e.GetProperty("name").GetString()).Order());
        Assert.Equal(4, copies.Select(Id).Distinct().Count());
        foreach (var copy in copies)
        {
            var link = await Json(await member.GetAsync($"/files/{Id(copy)}/download"));
            Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        }
        var existing = (await Root(member)).Single(e => e.GetProperty("name").GetString() == "photo (1).jpg");
        var existingLink = await Json(await member.GetAsync($"/files/{Id(existing)}/download"));
        Assert.Equal("do not replace"u8.ToArray(), await Transfer.GetByteArrayAsync(existingLink.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Copying_a_folder_into_a_descendant_uses_a_finite_snapshot()
    {
        var (_, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Tree" })));
        var child = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Child", parentId = folder })));
        await Upload(member, "file.jpg", child);
        var copy = await Json(await member.PostAsJsonAsync($"/elements/{folder}/copy", new { parentId = child }));
        Assert.Equal(child, copy.GetProperty("parentId").GetGuid());
        var copiedChild = Assert.Single((await Json(await member.GetAsync($"/folders/{Id(copy)}/elements"))).EnumerateArray());
        var copiedFile = Assert.Single((await Json(await member.GetAsync($"/folders/{Id(copiedChild)}/elements"))).EnumerateArray());
        Assert.Equal("file.jpg", copiedFile.GetProperty("name").GetString());
        var link = await Json(await member.GetAsync($"/files/{Id(copiedFile)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.Conflict)]
    public async Task Failed_tree_copy_publishes_nothing_and_cleans_previously_copied_objects(bool changedSize, HttpStatusCode expected)
    {
        var (_, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Tree" })));
        var child = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Child", parentId = folder })));
        await Upload(member, "first.jpg", folder);
        var broken = Id(await Upload(member, "broken.jpg", child));
        var link = await Json(await member.GetAsync($"/files/{broken}/download"));
        var key = new Uri(link.GetProperty("url").GetString()!).AbsolutePath.Split('/', 3)[2];
        if (changedSize) await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, ContentBody = "wrong size" });
        else await S3.DeleteObjectAsync(Bucket, key);
        var before = (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects.Select(o => o.Key).Order().ToArray();
        var response = await member.PostAsJsonAsync($"/elements/{folder}/copy", new { });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(changedSize ? "source_changed" : "storage_unavailable", await Error(response));
        Assert.Equal(folder, Id(Assert.Single(await Root(member))));
        Assert.Equal(before, (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects.Select(o => o.Key).Order());
    }
}
