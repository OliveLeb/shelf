using Npgsql;

namespace Shelf.Api.Storage;

/// <summary>Background task that purges expired or orphaned uploads: it aborts their multipart uploads and deletes their objects.</summary>
public sealed class UploadCleanup(NpgsqlDataSource source, ObjectStore store, TimeProvider clock, ILogger<UploadCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(store.CleanupInterval, clock);
        while (await timer.WaitForNextTickAsync(stopping))
        {
            try { while (await PurgeOne(stopping)) { } }
            // A failed round leaves the upload in place, so the next round retries it.
            catch (Exception error) when (!stopping.IsCancellationRequested) { logger.LogWarning("Upload cleanup failed: {Error}", error.GetType().Name); }
        }
    }

    private async Task<bool> PurgeOne(CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        var purged = await new StorageTransaction(connection, transaction, store, clock, cancellation).PurgeAbandonedUpload();
        await transaction.CommitAsync(cancellation);
        return purged;
    }
}
