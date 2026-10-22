using Amazon.Runtime;
using Npgsql;

namespace Shelf.Api.Storage;

/// <summary>Metadata expiry and durable, idempotent S3 deletion. Multiple API replicas can drain the outbox.</summary>
public sealed class TrashWorker(NpgsqlDataSource source, ObjectStore store, TimeProvider clock, ILogger<TrashWorker> log) : BackgroundService
{
    private string? continuation;
    private DateTimeOffset nextReconciliation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                await Expire(stoppingToken);
                for (var count = 0; count < 100 && await DeleteNext(stoppingToken); count++) { }
                if (clock.GetUtcNow() >= nextReconciliation) await Reconcile(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException error) { log.LogWarning("storage_cleanup_database_retry {SqlState}", error.SqlState); }
            catch (NpgsqlException) { log.LogWarning("storage_cleanup_database_retry"); }
            catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException
                || error is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("object_reconciliation_retry");
                continuation = null;
                nextReconciliation = clock.GetUtcNow().AddMinutes(1);
            }
        }
    }

    private async Task Reconcile(CancellationToken cancellation)
    {
        var page = await store.ListFiles(continuation, cancellation);
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        // A copy's objects are visible in S3 before its metadata commits. Wait before taking
        // the reference snapshot, including when another API replica is performing the copy.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            gate.Parameters.AddWithValue(StorageTransaction.CopyReconciliationLock);
            await gate.ExecuteNonQueryAsync(cancellation);
        }
        // An old signed PUT (including one finishing after URL expiry) can recreate an acknowledged deletion.
        // Upload keys commit before their URLs are returned; confirmation transfers ownership atomically.
        // Use one DB snapshot for both owners, including trashed files and unconfirmed uploads.
        await using var command = new NpgsqlCommand("""
            INSERT INTO object_deletions (object_key)
            SELECT object_key FROM unnest($1::text[]) AS objects(object_key)
            WHERE NOT EXISTS (SELECT 1 FROM elements e WHERE e.object_key=objects.object_key)
              AND NOT EXISTS (SELECT 1 FROM uploads u WHERE u.object_key=objects.object_key)
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue(page.Keys);
        await command.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        continuation = page.Continuation;
        // Bound each tick to one page; repeat full scans indefinitely, including after host restarts.
        nextReconciliation = continuation is null ? clock.GetUtcNow().AddMinutes(1) : clock.GetUtcNow();
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
