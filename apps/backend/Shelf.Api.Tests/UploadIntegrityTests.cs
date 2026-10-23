using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class UploadIntegrityTests : TestInstance
{
    [Theory]
    [InlineData("revoked", "tampered")]
    [InlineData("revoked", "a larger replacement")]
    [InlineData("read", "tampered")]
    [InlineData("read", "a larger replacement")]
    public async Task Replaying_an_upload_after_permission_withdrawal_cannot_change_published_content(string change, string replay)
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var folder = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Shared" })));
        await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "write" }));
        var upload = await Declare(recipient, "file.bin", 8, "application/octet-stream", folder);
        await Put(upload, "original"u8.ToArray());
        var file = await Json(await Confirm(recipient, upload));
        if (change == "revoked")
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{folder}/shares/{recipientId}")).StatusCode);
        else
            await Json(await owner.PutAsJsonAsync($"/elements/{folder}/shares/{recipientId}", new { level = "read" }));

        await Replay(upload, replay);

        var listed = Assert.Single((await Json(await owner.GetAsync($"/folders/{folder}/elements"))).EnumerateArray());
        Assert.Equal(Id(file), Id(listed));
        Assert.Equal(8, listed.GetProperty("size").GetInt64());
        var download = await Json(await owner.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal("original"u8.ToArray(), await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        var usage = await Json(await owner.GetAsync("/storage/usage"));
        Assert.Equal(8, usage.GetProperty("personalBytes").GetInt64());
        Assert.Empty((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.DoesNotContain(Logs.Messages, message => message.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase)
            || message.Contains(S3SecretKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legitimate_targeted_replacement_preserves_identity_and_shares_and_replays_cannot_change_either_snapshot()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var original = await Declare(owner, "file.bin", 8, "application/octet-stream");
        await Put(original, "original"u8.ToArray());
        var file = await Json(await Confirm(owner, original));
        await Json(await owner.PutAsJsonAsync($"/elements/{Id(file)}/shares/{recipientId}", new { level = "write" }));
        var replacement = await Json(await recipient.PostAsJsonAsync("/uploads",
            new { replaceFileId = Id(file), size = 11, contentType = "application/octet-stream" }));
        await Put(replacement, "replacement"u8.ToArray());
        var replaced = await Json(await Confirm(recipient, replacement));
        Assert.Equal(Id(file), Id(replaced));
        Assert.Equal(11, replaced.GetProperty("size").GetInt64());
        var share = Assert.Single((await Json(await owner.GetAsync($"/elements/{Id(file)}/shares"))).EnumerateArray());
        Assert.Equal(recipientId, share.GetProperty("recipient").GetProperty("id").GetGuid());
        Assert.Equal("write", share.GetProperty("level").GetString());
        await Replay(original, "tampered");
        await Replay(replacement, "a larger replacement");

        Assert.Equal(19, (await Json(await owner.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        var trash = Assert.Single((await Json(await owner.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal(8, trash.GetProperty("element").GetProperty("size").GetInt64());
        var restored = await Json(await owner.PostAsync($"/trash/{Id(trash)}/restore", null));
        Assert.NotEqual(Id(file), Id(restored));
        foreach (var (actor, id, bytes) in new[] { (recipient, Id(file), "replacement"u8.ToArray()), (owner, Id(restored), "original"u8.ToArray()) })
        {
            var download = await Json(await actor.GetAsync($"/files/{id}/download"));
            Assert.Equal(bytes, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await recipient.GetAsync($"/files/{Id(restored)}/download")).StatusCode);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("a larger replacement")]
    public async Task Replay_between_validation_and_publication_fails_cleanly_and_can_be_retried(string replay)
    {
        await using var proxy = await S3Proxy.Start();
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:S3:Endpoint"] = proxy.Endpoint })));
        var (_, member) = await User();
        var upload = await Declare(member, "file.bin", 8, "application/octet-stream");
        await Put(upload, "original"u8.ToArray());
        var intercepted = 0;
        proxy.Intercept = async context =>
        {
            if (context.Request.Headers.ContainsKey("x-amz-copy-source") && Interlocked.Increment(ref intercepted) == 1)
                await Replay(upload, replay);
            return false;
        };

        var failed = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("source_changed", await Error(failed));
        Assert.Empty(await Root(member));
        Assert.Equal(0, (await Json(await member.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        Assert.Empty((await Json(await member.GetAsync("/spaces/personal/trash"))).EnumerateArray());
        Assert.Equal(1, intercepted);

        await Put(upload, "restored"u8.ToArray());
        var file = await Json(await Confirm(member, upload));
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal("restored"u8.ToArray(), await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task A_source_change_between_multipart_copy_parts_cannot_publish_mixed_content()
    {
        await using var proxy = await S3Proxy.Start();
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:S3:Endpoint"] = proxy.Endpoint,
                ["Storage:S3:SingleCopyLimitBytes"] = "5242880",
                ["Storage:S3:CopyPartSizeBytes"] = "5242880"
            })));
        var (_, member) = await User();
        var bytes = new byte[10 * 1024 * 1024 + 17];
        bytes[0] = 19;
        bytes[^1] = 73;
        var upload = await Declare(member, "file.bin", bytes.Length, "application/octet-stream");
        await Put(upload, bytes);
        var temporary = new Uri(upload.GetProperty("parts")[0].GetProperty("url").GetString()!).AbsolutePath.Split('/', 3)[2];
        var changed = new byte[bytes.Length];
        Array.Fill(changed, (byte)41);
        var faultApplied = false;
        proxy.Intercept = async context =>
        {
            if (context.Request.Headers.ContainsKey("x-amz-copy-source") && context.Request.Query["partNumber"] == "2" && !faultApplied)
            {
                faultApplied = true;
                // Provider-side fault: client multipart capabilities are already consumed at assembly.
                await S3.PutObjectAsync(new()
                {
                    BucketName = Bucket, Key = temporary, InputStream = new MemoryStream(changed), ContentType = "application/octet-stream"
                });
            }
            return false;
        };
        var failed = await Confirm(member, upload);
        Assert.True(faultApplied);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("source_changed", await Error(failed));
        Assert.Empty(await Root(member));
        Assert.Empty((await S3.ListMultipartUploadsAsync(Bucket)).MultipartUploads ?? []);
        Assert.Single(await Objects());

        var file = await Json(await Confirm(member, upload));
        Assert.Equal(bytes.Length, file.GetProperty("size").GetInt64());
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal(changed, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        await Eventually(async () => (await Objects()).Length == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_commit_is_invisible_and_restart_reconciles_the_final_object_without_losing_the_resumable_upload(bool multipart)
    {
        var (_, member) = await User();
        var bytes = multipart ? new byte[5 * 1024 * 1024 + 19] : "original"u8.ToArray();
        bytes[^1] = 73;
        var upload = await Declare(member, "file.bin", bytes.Length, "application/octet-stream");
        await Put(upload, bytes);
        var temporary = new Uri(upload.GetProperty("parts")[0].GetProperty("url").GetString()!).AbsolutePath.Split('/', 3)[2];
        await Sql("""
            CREATE FUNCTION reject_upload_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER reject_upload_commit AFTER INSERT ON elements
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_upload_commit();
            """);

        var failed = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("database_unavailable", await Error(failed));
        Assert.Empty(await Root(member));
        Assert.Equal(0, (await Json(await member.GetAsync("/storage/usage"))).GetProperty("personalBytes").GetInt64());
        Assert.Equal(2, (await Objects()).Length);
        // The assembled multipart upload is consumed, but its temporary object remains resumable.
        Assert.Empty((await S3.ListMultipartUploadsAsync(Bucket)).MultipartUploads ?? []);

        await RestartApp();
        member = await SignIn("member");
        await Eventually(async () => (await Objects()).SequenceEqual(new[] { temporary }));
        var resumed = await Json(await member.GetAsync($"/uploads/{Id(upload)}"));
        Assert.Empty(resumed.GetProperty("parts").EnumerateArray());
        await Sql("DROP TRIGGER reject_upload_commit ON elements");
        var file = await Json(await Confirm(member, resumed));
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        var final = new Uri(download.GetProperty("url").GetString()!).AbsolutePath.Split('/', 3)[2];
        await Eventually(async () => (await Objects()).SequenceEqual(new[] { final }));

        if (!multipart)
        {
            // A PUT finishing late may recreate an already acknowledged temporary deletion.
            await Replay(upload, "late write");
            Assert.Equal(2, (await Objects()).Length);
            await RestartApp();
            member = await SignIn("member");
            await Eventually(async () => (await Objects()).SequenceEqual(new[] { final }));
        }
        Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(member, upload)).StatusCode);
        Assert.Equal(bytes, await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
        Assert.Equal(Id(file), Id(Assert.Single(await Root(member))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3_publication_failure_can_be_retried_and_temporary_deletion_survives_failure_and_restart(bool copiedBeforeFailure)
    {
        await using var proxy = await S3Proxy.Start();
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:S3:Endpoint"] = proxy.Endpoint })));
        var (_, member) = await User();
        var upload = await Declare(member, "file.bin", 8, "application/octet-stream");
        await Put(upload, "original"u8.ToArray());
        static async Task<bool> Fail(HttpContext context)
        {
            context.Response.Clear();
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/xml";
            await context.Response.WriteAsync("<Error><Code>ServiceUnavailable</Code></Error>");
            return true;
        }
        proxy.Intercept = context => context.Request.Method == "DELETE"
            || !copiedBeforeFailure && context.Request.Headers.ContainsKey("x-amz-copy-source")
            ? Fail(context) : Task.FromResult(false);
        if (copiedBeforeFailure) proxy.AfterCopy = Fail;
        var failed = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("storage_unavailable", await Error(failed));
        Assert.Empty(await Root(member));
        Assert.Equal(copiedBeforeFailure ? 2 : 1, (await Objects()).Length);
        proxy.AfterCopy = _ => Task.FromResult(false);
        proxy.Intercept = context => context.Request.Method == "DELETE" ? Fail(context) : Task.FromResult(false);
        var file = await Json(await Confirm(member, upload));
        await Eventually(() => Task.FromResult(Logs.Messages.Any(message => message.Contains("object_deletion_retry", StringComparison.Ordinal))));
        Assert.Equal(copiedBeforeFailure ? 3 : 2, (await Objects()).Length);
        // Restart with the healthy S3 endpoint and advance past the durable deletion retry.
        await RestartApp();
        member = await SignIn("member");
        Clock.Advance(TimeSpan.FromHours(1));
        await Eventually(async () => (await Objects()).Length == 1);
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal("original"u8.ToArray(), await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task Reconciliation_waits_for_in_flight_publication_and_preserves_the_committed_object()
    {
        await using var proxy = await S3Proxy.Start();
        await RestartApp(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:S3:Endpoint"] = proxy.Endpoint })));
        var (_, member) = await User();
        var upload = await Declare(member, "file.bin", 8, "application/octet-stream");
        await Put(upload, "original"u8.ToArray());
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterCopy = async _ =>
        {
            copied.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return false;
        };
        var confirming = Confirm(member, upload);
        JsonElement file;
        try
        {
            await copied.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await S3.PutObjectAsync(new() { BucketName = Bucket, Key = "files/probe", ContentBody = "orphan" });
            Clock.Advance(TimeSpan.FromMinutes(2));
            // Observe the real cleanup transaction waiting, not an arbitrary sleep or latency threshold.
            await using var connection = await App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
            await Eventually(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')", connection);
                return (bool)(await command.ExecuteScalarAsync())!;
            });
        }
        finally { release.TrySetResult(); }
        file = await Json(await confirming);
        await Eventually(async () => !(await Objects()).Contains("files/probe") && (await Objects()).Length == 1);
        var download = await Json(await member.GetAsync($"/files/{Id(file)}/download"));
        Assert.Equal("original"u8.ToArray(), await Transfer.GetByteArrayAsync(download.GetProperty("url").GetString()));
    }

    private async Task<string[]> Objects() => [.. ((await S3.ListObjectsV2Async(new() { BucketName = Bucket })).S3Objects ?? []).Select(o => o.Key)];

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(20); !await condition(); await Task.Delay(50))
            Assert.True(DateTime.UtcNow < deadline, "Storage condition not reached in time");
    }

    // A wire-level gate/fault injector: all successful transfers still go to real MinIO.
    // Preserve Host and the signed headers when forwarding; never log URLs or credentials.
    private sealed class S3Proxy(WebApplication app) : IAsyncDisposable
    {
        private readonly HttpClient upstream = new();
        public string Endpoint => app.Urls.Single();
        public Func<HttpContext, Task<bool>> Intercept { get; set; } = _ => Task.FromResult(false);
        public Func<HttpContext, Task<bool>> AfterCopy { get; set; } = _ => Task.FromResult(false);

        public static async Task<S3Proxy> Start()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var proxy = new S3Proxy(app);
            var target = Environment.GetEnvironmentVariable("SHELF_TEST_S3_ENDPOINT") ?? "http://localhost:9000";
            app.Run(async context =>
            {
                if (await proxy.Intercept(context)) return;
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                    target.TrimEnd('/') + context.Request.Path + context.Request.QueryString);
                if (context.Request.ContentLength > 0 || context.Request.Method is "PUT" or "POST")
                {
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body);
                    request.Content = new ByteArrayContent(body.ToArray());
                }
                foreach (var header in context.Request.Headers)
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                        request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                using var response = await proxy.upstream.SendAsync(request);
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Response.Headers.Remove("transfer-encoding");
                if (request.Headers.Contains("x-amz-copy-source") && response.IsSuccessStatusCode && await proxy.AfterCopy(context)) return;
                if (context.Request.Method != "HEAD") await response.Content.CopyToAsync(context.Response.Body);
            });
            await app.StartAsync();
            return proxy;
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            upstream.Dispose();
        }
    }

    private static async Task Replay(JsonElement upload, string bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.GetProperty("parts")[0].GetProperty("url").GetString())
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(bytes))
        };
        foreach (var header in upload.GetProperty("headers").EnumerateObject())
            request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
        using var response = await Transfer.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Replay: HTTP {(int)response.StatusCode}");
    }
}
