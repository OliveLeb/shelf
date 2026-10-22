using Amazon.Runtime;
using Npgsql;

namespace Shelf.Api.Storage;

/// <summary>Metadata expiry and durable, idempotent S3 deletion. Multiple API replicas can drain the outbox.</summary>
public sealed class TrashWorker(NpgsqlDataSource source, ObjectStore store, TimeProvider clock, ILogger<TrashWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                await Expire(stoppingToken);
                for (var count = 0; count < 100 && await DeleteNext(stoppingToken); count++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException error) { log.LogWarning("storage_cleanup_database_retry {SqlState}", error.SqlState); }
            catch (NpgsqlException) { log.LogWarning("storage_cleanup_database_retry"); }
        }
    }

    private async Task Expire(CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        // Same space lock and order as HTTP mutations: restoration and expiry cannot interleave.
        await using var command = new NpgsqlCommand("""
            SELECT id FROM spaces WHERE id IN (SELECT space_id FROM trash_entries WHERE deleted_at<=$1)
            ORDER BY id FOR NO KEY UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(clock.GetUtcNow().AddDays(-30));
        await command.ExecuteNonQueryAsync(cancellation);
        command.CommandText = "DELETE FROM trash_entries WHERE deleted_at<=$1";
        await command.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    private async Task<bool> DeleteNext(CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using var select = new NpgsqlCommand("""
            SELECT object_key FROM object_deletions WHERE next_attempt_at<=$1
            ORDER BY next_attempt_at, object_key LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction);
        select.Parameters.AddWithValue(clock.GetUtcNow());
        var key = (string?)await select.ExecuteScalarAsync(cancellation);
        if (key is null) return false;
        try
        {
            await store.Delete(key, cancellation);
            await using var remove = new NpgsqlCommand("DELETE FROM object_deletions WHERE object_key=$1", connection, transaction);
            remove.Parameters.AddWithValue(key);
            await remove.ExecuteNonQueryAsync(cancellation);
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            // Do not log provider exceptions, URLs or credentials. Failure of one key must not starve the rest.
            log.LogWarning("object_deletion_retry");
            await using var retry = new NpgsqlCommand("""
                UPDATE object_deletions SET attempts=attempts+1,
                    next_attempt_at=$2 + make_interval(secs => LEAST(3600, 30 * power(2, LEAST(attempts, 7)))::integer)
                WHERE object_key=$1
                """, connection, transaction);
            retry.Parameters.AddWithValue(key);
            retry.Parameters.AddWithValue(clock.GetUtcNow());
            await retry.ExecuteNonQueryAsync(cancellation);
        }
        // A crash between S3 DELETE and commit leaves the row for a safe retry, never a live element with missing content.
        await transaction.CommitAsync(cancellation);
        return true;
    }
}
