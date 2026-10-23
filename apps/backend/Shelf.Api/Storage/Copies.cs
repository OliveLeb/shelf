using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    // Shared by copies, exclusive to orphan reconciliation; unrelated to account-operation locks.
    internal const long CopyReconciliationLock = 742013511;

    public async Task<Element> Copy(Account account, Guid elementId, CopyRequest input)
    {
        // S3 objects become visible before the metadata transaction commits. Reconciliation must
        // wait for that commit (or rollback), rather than enqueue these objects as orphans.
        await Run("SELECT pg_advisory_xact_lock_shared($1)", CopyReconciliationLock);
        await LockSpaces(account, moving: true, elementId: elementId, folderId: input.ParentId,
            rootSpace: input.ParentId is null ? input.Space ?? "personal" : null);
        await Access(account, elementId);
        var destination = await Destination(account, input.ParentId, input.Space);

        // Materialize before inserting: even a copy into itself or a descendant is a finite snapshot.
        var tree = await Query("""
            WITH RECURSIVE subtree(id, depth) AS (
                SELECT id, 0 FROM elements WHERE id=$1 AND trash_id IS NULL
                UNION ALL SELECT e.id, p.depth+1 FROM elements e JOIN subtree p ON e.parent_id=p.id WHERE e.trash_id IS NULL)
            SELECT e.id, e.parent_id, e.kind, e.name, e.size, e.content_type, e.object_key
            FROM elements e JOIN subtree t ON t.id=e.id ORDER BY t.depth, e.id
            """, r => (Id: r.GetGuid(0), Parent: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), Kind: r.GetString(2), Name: r.GetString(3),
                Size: r.IsDBNull(4) ? (long?)null : r.GetInt64(4), ContentType: r.IsDBNull(5) ? null : r.GetString(5),
                Key: r.IsDBNull(6) ? null : r.GetString(6)), elementId);
        var ids = tree.ToDictionary(e => e.Id, _ => Guid.NewGuid());
        var copiedKeys = new List<string>();
        try
        {
            foreach (var element in tree)
            {
                var id = ids[element.Id];
                var parent = element.Id == elementId ? input.ParentId : ids[element.Parent!.Value];
                var key = element.Key is null ? null : ObjectStore.NewKey();
                // Reserve the root name through the same unique index used by upload "keep both".
                for (var number = 0; ; number++)
                {
                    if (await Scalar("""
                        INSERT INTO elements (id, space_id, parent_id, kind, name, size, content_type, object_key, added_at, added_by)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                        ON CONFLICT (space_id, parent_id, upper(name)) WHERE trash_id IS NULL DO NOTHING RETURNING id
                        """, id, destination, parent, element.Kind, Numbered(element.Name, number), element.Size,
                        element.ContentType, key, clock.GetUtcNow(), account.Id) is not null)
                        break;
                }
                if (key is not null)
                {
                    copiedKeys.Add(key);
                    try { await store.Copy(element.Key!, key, element.Size!.Value, cancellation); }
                    catch (Amazon.S3.AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                    { throw new StorageError(409, "source_changed"); }
                }
            }
            return await Get(ids[elementId]);
        }
        catch
        {
            // Rollback hides the entire tree; also remove any S3 objects already created.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var key in copiedKeys)
            {
                try { await store.Delete(key, cleanup.Token); }
                catch (Exception error) when (error is Amazon.Runtime.AmazonServiceException or HttpRequestException or OperationCanceledException)
                { /* An unavailable S3 may leave orphan objects; see the storage operations guide. */ }
            }
            throw;
        }
    }
}
