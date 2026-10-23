using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class CopyTests : TestInstance
{
    [Fact]
    public async Task Personal_copy_does_not_block_another_users_personal_write()
    {
        var (_, alice) = await User("alice");
        var (_, bob) = await User("bob");
        using var observer = await SignIn("alice", device: "observer");
        var source = Id(await Upload(alice, "photo.jpg"));
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(33001)", gate))
            await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION hold_copy_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.name='photo (1).jpg' THEN PERFORM pg_advisory_xact_lock(33001); END IF; RETURN NEW; END; $$;
            CREATE CONSTRAINT TRIGGER hold_copy_commit AFTER INSERT ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION hold_copy_commit();
            """);
        var copying = alice.PostAsJsonAsync($"/elements/{source}/copy", new { });
        try
        {
            await WaitForGate(gate, 33001);
            // The gate, not elapsed time, defines ordering. This deadline only detects a stuck request.
            var folder = await Json(await bob.PostAsJsonAsync("/folders", new { name = "Independent" }).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Id(folder), Id(Assert.Single(await Root(bob))));
            Assert.Equal(source, Id(Assert.Single(await Root(observer))));
            Assert.False(copying.IsCompleted);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(33001)", gate);
            await command.ExecuteNonQueryAsync();
        }
        var copy = await Json(await copying);
        Assert.Equal(2, (await Root(alice)).Length);
        var link = await Json(await alice.GetAsync($"/files/{Id(copy)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Copy_relocks_a_source_or_destination_moved_to_common_while_waiting(bool movingSource)
    {
        var (_, alice) = await User("alice");
        var (bobId, bob) = await User("bob");
        var folder = Id(await Json(await alice.PostAsJsonAsync("/folders", new { name = "Moving" })));
        var sharedFile = Id(await Upload(alice, "probe.jpg", folder));
        var ownFile = Id(await Upload(bob, "probe.jpg"));
        await Json(await alice.PutAsJsonAsync($"/elements/{folder}/shares/{bobId}", new { level = "write" }));
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(33002), pg_advisory_lock(33003)", gate))
            await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION gate_space_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(33002); RETURN NEW; END; $$;
            CREATE CONSTRAINT TRIGGER gate_space_change AFTER UPDATE ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                WHEN (NEW.space_id IS DISTINCT FROM OLD.space_id) EXECUTE FUNCTION gate_space_change();
            CREATE FUNCTION gate_copy_publication() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(33003); RETURN NEW; END; $$;
            CREATE CONSTRAINT TRIGGER gate_copy_publication AFTER INSERT ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gate_copy_publication();
            """);
        var moving = alice.PostAsJsonAsync($"/elements/{folder}/move", new { space = "common" });
        Task<HttpResponseMessage> copying;
        Task<HttpResponseMessage> deleting;
        try
        {
            await WaitForGate(gate, 33002);
            copying = movingSource
                ? bob.PostAsJsonAsync($"/elements/{sharedFile}/copy", new { })
                : bob.PostAsJsonAsync($"/elements/{ownFile}/copy", new { parentId = folder });
            await WaitForSpaceLock(gate);
            await using (var command = new NpgsqlCommand("SELECT pg_advisory_unlock(33002)", gate))
                await command.ExecuteNonQueryAsync();
            await Json(await moving);
            await WaitForGate(gate, 33003);
            // A real conflicting operation in the NEW space must still wait for publication.
            deleting = Admin.DeleteAsync($"/elements/{folder}");
            await WaitForSpaceLock(gate);
            Assert.False(copying.IsCompleted);
            Assert.False(deleting.IsCompleted);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", gate);
            await command.ExecuteNonQueryAsync();
        }
        var copy = await Json(await copying);
        Assert.Equal(HttpStatusCode.NoContent, (await deleting).StatusCode);
        Assert.Empty((await Json(await bob.GetAsync("/shares/received"))).EnumerateArray());
        if (movingSource)
        {
            var link = await Json(await bob.GetAsync($"/files/{Id(copy)}/download"));
            Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/files/{Id(copy)}/download")).StatusCode);
            await Json(await Admin.PostAsync($"/trash/{folder}/restore", null));
            var children = (await Json(await Admin.GetAsync($"/folders/{folder}/elements"))).EnumerateArray().ToArray();
            Assert.Equal(new[] { "probe (1).jpg", "probe.jpg" }, children.Select(e => e.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
            var link = await Json(await Admin.GetAsync($"/files/{Id(copy)}/download"));
            Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
        }
    }

    [Theory]
    [InlineData(true, false, HttpStatusCode.NotFound)]
    [InlineData(false, false, HttpStatusCode.NotFound)]
    [InlineData(false, true, HttpStatusCode.Forbidden)]
    public async Task Copy_rechecks_source_and_destination_permissions_after_waiting(bool sharedSource, bool downgrade, HttpStatusCode expected)
    {
        var (_, alice) = await User("alice");
        var (bobId, bob) = await User("bob");
        var folder = Id(await Json(await alice.PostAsJsonAsync("/folders", new { name = "Shared" })));
        var sharedFile = Id(await Upload(alice, "shared.jpg", folder));
        var ownFile = Id(await Upload(bob, "own.jpg"));
        await Json(await alice.PutAsJsonAsync($"/elements/{folder}/shares/{bobId}", new { level = "write" }));
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(33004)", gate))
            await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION gate_share_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(33004); RETURN NULL; END; $$;
            CREATE TRIGGER gate_share_change AFTER UPDATE OR DELETE ON shares
                FOR EACH ROW EXECUTE FUNCTION gate_share_change();
            """);
        var changing = downgrade
            ? alice.PutAsJsonAsync($"/elements/{folder}/shares/{bobId}", new { level = "read" })
            : alice.DeleteAsync($"/elements/{folder}/shares/{bobId}");
        Task<HttpResponseMessage> copying;
        try
        {
            await WaitForGate(gate, 33004);
            copying = sharedSource
                ? bob.PostAsJsonAsync($"/elements/{sharedFile}/copy", new { })
                : bob.PostAsJsonAsync($"/elements/{ownFile}/copy", new { parentId = folder });
            await WaitForSpaceLock(gate);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(33004)", gate);
            await command.ExecuteNonQueryAsync();
        }
        Assert.True((await changing).IsSuccessStatusCode);
        var response = await copying;
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(downgrade ? "write_forbidden" : sharedSource ? "element_not_found" : "folder_not_found", await Error(response));
        Assert.Equal(ownFile, Id(Assert.Single(await Root(bob))));
        Assert.Equal(sharedFile, Id(Assert.Single((await Json(await alice.GetAsync($"/folders/{folder}/elements"))).EnumerateArray())));
    }

    private static Task WaitForGate(NpgsqlConnection connection, int id) => Eventually(async () =>
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=$1 AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database()))", connection);
        command.Parameters.AddWithValue(id);
        return (bool)(await command.ExecuteScalarAsync())!;
    });

    private static Task WaitForSpaceLock(NpgsqlConnection connection) => Eventually(async () =>
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'SELECT id FROM spaces%')", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    });

    [Fact]
    public async Task Same_session_reads_complete_before_a_gated_copy_is_published()
    {
        var (_, member) = await User();
        using var control = await SignIn("member");
        var sessions = await Json(await member.GetAsync("/account/sessions"));
        var sessionId = sessions.EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();
        var startedAt = Clock.GetUtcNow();
        var source = Id(await Upload(member, "photo.jpg"));
        await using var gate = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        // Holding the account-operations lock must not stop storage authentication or reads.
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(340034); SELECT pg_advisory_lock(742013510)", gate)) await command.ExecuteNonQueryAsync();
        await Sql("""
            CREATE FUNCTION gate_copy() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.name='photo (1).jpg' THEN PERFORM pg_advisory_xact_lock(340034); END IF; RETURN NEW; END; $$;
            CREATE CONSTRAINT TRIGGER gate_copy AFTER INSERT ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gate_copy();
            """);
        var copying = member.PostAsJsonAsync($"/elements/{source}/copy", new { });
        try
        {
            await Eventually(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=340034 AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND NOT granted)", gate);
                return (bool)(await command.ExecuteScalarAsync())!;
            });
            // The other session is only a control; both reads must finish while publication is held.
            Assert.Equal(source, Id(Assert.Single(await Root(control).WaitAsync(TimeSpan.FromSeconds(15)))));
            Clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(source, Id(Assert.Single(await Root(member).WaitAsync(TimeSpan.FromSeconds(15)))));
            // An older timestamp must not undo the newer activity, even during another operation.
            Clock.Advance(TimeSpan.FromDays(-1));
            Assert.Equal(source, Id(Assert.Single(await Root(member).WaitAsync(TimeSpan.FromSeconds(15)))));
            Assert.False(copying.IsCompleted);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(340034); SELECT pg_advisory_unlock(742013510)", gate);
            await command.ExecuteNonQueryAsync();
        }
        var copy = await Json(await copying);
        var activity = (await Json(await control.GetAsync("/account/sessions"))).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sessionId);
        Assert.InRange(startedAt.AddDays(2) - activity.GetProperty("lastSeenAt").GetDateTimeOffset(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        Assert.InRange(startedAt.AddDays(32) - activity.GetProperty("expiresAt").GetDateTimeOffset(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
        Assert.Equal(2, (await Root(member)).Length);
        var link = await Json(await member.GetAsync($"/files/{Id(copy)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

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
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => member.PostAsJsonAsync($"/elements/{file}/copy", new { })));
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
        await Eventually(async () => (await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects.Count == 2);
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

    [Fact]
    public async Task Copy_into_a_write_shared_folder_belongs_to_its_owner_without_copying_source_grants()
    {
        var (_, sourceOwner) = await User("source");
        var (_, destinationOwner) = await User("destination");
        var (copierId, copier) = await User("copier");
        var (readerId, reader) = await User("reader");
        var source = Id(await Upload(sourceOwner, "shared.jpg"));
        await Json(await sourceOwner.PutAsJsonAsync($"/elements/{source}/shares/{copierId}", new { level = "read" }));
        await Json(await sourceOwner.PutAsJsonAsync($"/elements/{source}/shares/{readerId}", new { level = "read" }));
        var folder = Id(await Json(await destinationOwner.PostAsJsonAsync("/folders", new { name = "Destination" })));
        await Json(await destinationOwner.PutAsJsonAsync($"/elements/{folder}/shares/{copierId}", new { level = "write" }));
        var copy = await Json(await copier.PostAsJsonAsync($"/elements/{source}/copy", new { parentId = folder }));
        Assert.Equal(folder, copy.GetProperty("parentId").GetGuid());
        Assert.Equal(copierId, copy.GetProperty("addedBy").GetProperty("id").GetGuid());
        var grant = Assert.Single((await Json(await destinationOwner.GetAsync($"/elements/{Id(copy)}/shares"))).EnumerateArray());
        Assert.Equal(folder, grant.GetProperty("elementId").GetGuid());
        Assert.Equal(copierId, grant.GetProperty("recipient").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/files/{Id(copy)}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sourceOwner.GetAsync($"/files/{Id(copy)}/download")).StatusCode);
        await destinationOwner.DeleteAsync($"/elements/{folder}/shares/{copierId}");
        Assert.Equal(HttpStatusCode.NotFound, (await copier.GetAsync($"/files/{Id(copy)}/download")).StatusCode);
        var link = await Json(await destinationOwner.GetAsync($"/files/{Id(copy)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Trash_is_not_copied_or_used_as_a_destination_and_purging_either_copy_preserves_the_other()
    {
        var (_, member) = await User();
        var folder = Id(await Json(await member.PostAsJsonAsync("/folders", new { name = "Tree" })));
        var removed = Id(await Upload(member, "removed.jpg", folder));
        var kept = Id(await Upload(member, "kept.jpg", folder));
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/elements/{removed}")).StatusCode);
        var copy = Id(await Json(await member.PostAsJsonAsync($"/elements/{folder}/copy", new { })));
        var copiedFile = Assert.Single((await Json(await member.GetAsync($"/folders/{copy}/elements"))).EnumerateArray());
        Assert.Equal("kept.jpg", copiedFile.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync($"/elements/{removed}/copy", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/elements/{copy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync($"/elements/{kept}/copy", new { parentId = copy })).StatusCode);
        var replacement = Id(await Json(await member.PostAsJsonAsync($"/elements/{folder}/copy", new { })));
        Assert.Equal("Tree (1)", (await Json(await member.GetAsync("/spaces/personal/elements"))).EnumerateArray().Single(e => Id(e) == replacement).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/trash/{copy}")).StatusCode);
        var originalLink = await Json(await member.GetAsync($"/files/{kept}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(originalLink.GetProperty("url").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/elements/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/trash/{folder}")).StatusCode);
        var replacementFile = Assert.Single((await Json(await member.GetAsync($"/folders/{replacement}/elements"))).EnumerateArray());
        var link = await Json(await member.GetAsync($"/files/{Id(replacementFile)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Reconciliation_during_a_slow_copy_does_not_delete_the_uncommitted_object()
    {
        var (_, member) = await User();
        var source = Id(await Upload(member, "photo.jpg"));
        await Eventually(async () => (await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects.Count == 1);
        // Delay publication after the real S3 copy completes, reproducing a slow database commit.
        await Sql("""
            CREATE FUNCTION slow_copy_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.name='photo (1).jpg' THEN PERFORM pg_sleep(6); END IF; RETURN NEW; END; $$;
            CREATE CONSTRAINT TRIGGER slow_copy_commit AFTER INSERT ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION slow_copy_commit();
            """);
        var copying = member.PostAsJsonAsync($"/elements/{source}/copy", new { });
        await Eventually(async () => (await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects.Count == 2);
        const string orphan = "files/reconciliation-probe";
        await S3.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = orphan, ContentBody = "orphan" });
        Clock.Advance(TimeSpan.FromMinutes(2));
        var copy = await Json(await copying);
        // The probe disappearing proves that reconciliation and deletion actually ran, not just that time passed.
        await Eventually(async () => !(await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket })).S3Objects.Any(o => o.Key == orphan));
        var link = await Json(await member.GetAsync($"/files/{Id(copy)}/download"));
        Assert.Equal(Photo, await Transfer.GetByteArrayAsync(link.GetProperty("url").GetString()));
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            if (await condition()) return;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail("Storage work did not complete within 15 seconds.");
    }
}
