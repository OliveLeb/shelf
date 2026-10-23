using System.Diagnostics;
using Npgsql;

namespace Shelf.Api.Storage;

/// <summary>One bounded generation attempt per content version; PostgreSQL owns the queue across host restarts.</summary>
public sealed class ThumbnailWorker(NpgsqlDataSource source, ObjectStore store, TimeProvider clock, ILogger<ThumbnailWorker> log) : BackgroundService
{
    private sealed record Work(Guid Id, string Key, string ContentType, string ThumbnailKey);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                if (await Claim(stoppingToken) is { } work) await Generate(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NpgsqlException) { log.LogWarning("thumbnail_database_retry"); }
        }
    }

    private async Task<Work?> Claim(CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        // A host killed during generation cannot leave a file pending forever, or retry a poison file forever.
        await using var expire = new NpgsqlCommand("""
            UPDATE elements SET thumbnail_state='unavailable'
            WHERE thumbnail_state='pending' AND thumbnail_started_at <= $1
            """, connection);
        expire.Parameters.AddWithValue(clock.GetUtcNow().AddMinutes(-5));
        await expire.ExecuteNonQueryAsync(cancellation);
        await using var claim = new NpgsqlCommand("""
            UPDATE elements SET thumbnail_started_at=$1, thumbnail_key=$2
            WHERE id IN (SELECT id FROM elements WHERE thumbnail_state='pending' AND thumbnail_started_at IS NULL
                ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, object_key, content_type, thumbnail_key
            """, connection);
        claim.Parameters.AddWithValue(clock.GetUtcNow());
        claim.Parameters.AddWithValue(ObjectStore.NewKey());
        await using var reader = await claim.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }

    private async Task Generate(Work work, CancellationToken stoppingToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "shelf-preview-" + Guid.NewGuid().ToString("N"));
        var available = false;
        try
        {
            var type = work.ContentType.Split(';')[0].Trim().ToLowerInvariant();
            if (type is not ("image/jpeg" or "image/heic" or "image/heif") && !type.StartsWith("video/", StringComparison.Ordinal))
                return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var input = Path.Combine(directory, "source");
            var output = Path.Combine(directory, "preview.jpg");
            if (type.StartsWith("video/", StringComparison.Ordinal))
                // ffmpeg seeks directly over S3 Range requests, including to a trailing MP4 index.
                // Do not download multi-gigabyte videos to the VPS just to extract the first frame.
                await Run("ffmpeg", ["-nostdin", "-v", "error", "-threads", "1", "-filter_threads", "1", "-max_alloc", "67108864",
                    "-protocol_whitelist", "http,https,tcp,tls", "-format_whitelist", "mov,matroska,webm,avi,mpeg,mpegts,ogg", "-i", store.SignedInline(work.Key).Url,
                    "-frames:v", "1", "-vf", "scale=320:320:force_original_aspect_ratio=decrease", "-an", output], deadline.Token);
            else
            {
                await store.FetchPreviewSource(work.Key, input, deadline.Token);
                await Run("convert", ["-limit", "thread", "1", "-limit", "memory", "64MiB", "-limit", "map", "128MiB", "-limit", "disk", "256MiB",
                    (type == "image/jpeg" ? "jpeg:" : "heic:") + input + "[0]", "-auto-orient", "-thumbnail", "320x320>",
                    "-strip", "-quality", "80", output], deadline.Token);
            }
            // Coordinate with orphan reconciliation if purge/replacement removes our reservation while generating.
            await using var connection = await source.OpenConnectionAsync(deadline.Token);
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
            await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared($1)", connection, transaction);
            gate.Parameters.AddWithValue(StorageTransaction.ObjectPublicationReconciliationLock);
            await gate.ExecuteNonQueryAsync(deadline.Token);
            await store.PutThumbnail(work.ThumbnailKey, output, deadline.Token);
            await Finish(work, true, connection, transaction, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            available = true;
        }
        catch (Exception error) when (error is not NpgsqlException)
        {
            // Decoder/provider errors can contain paths, URLs or credentials; never log their text.
            log.LogWarning("thumbnail_unavailable");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { log.LogWarning("thumbnail_temp_cleanup_failed"); }
            catch (UnauthorizedAccessException) { log.LogWarning("thumbnail_temp_cleanup_failed"); }
            if (!available && !stoppingToken.IsCancellationRequested)
            {
                await using var connection = await source.OpenConnectionAsync(stoppingToken);
                await Finish(work, false, connection, null, stoppingToken);
            }
        }
    }

    private static async Task Finish(Work work, bool available, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellation)
    {
        // Never attach old content's miniature to a replacement, or resurrect a purged file.
        await using var command = new NpgsqlCommand("""
            UPDATE elements SET thumbnail_state=$4 WHERE id=$1 AND object_key=$2 AND thumbnail_key=$3 AND thumbnail_state='pending'
            """, connection, transaction);
        command.Parameters.AddWithValue(work.Id);
        command.Parameters.AddWithValue(work.Key);
        command.Parameters.AddWithValue(work.ThumbnailKey);
        command.Parameters.AddWithValue(available ? "available" : "unavailable");
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private static async Task Run(string executable, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        try { await process.WaitForExitAsync(cancellation); }
        catch
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        await Task.WhenAll(stderr, stdout);
        if (process.ExitCode != 0) throw new InvalidDataException("preview_decoder_failed");
    }
}
