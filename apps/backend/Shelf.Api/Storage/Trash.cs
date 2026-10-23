using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    public async Task Delete(Account account, Guid id)
    {
        await LockSpaces(account, moving: true, elementId: id);
        var access = await Access(account, id);
        await RequireMutableSubtree(account, id, access);
        await SendToTrash(account, id, access.Space);
    }

    private async Task SendToTrash(Account account, Guid id, Guid space)
    {
        await RecordDeletion(account, id, id, space);
        // Detach only the root: an independently deleted child must not follow a later deletion of its old parent.
        await Run("""
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM elements WHERE id=$1
                UNION ALL SELECT e.id FROM elements e JOIN subtree p ON e.parent_id=p.id)
            UPDATE elements SET trash_id=$1, parent_id=CASE WHEN id=$1 THEN NULL ELSE parent_id END
            WHERE id IN (SELECT id FROM subtree)
            """, id);
    }

    private Task RecordDeletion(Account account, Guid sourceId, Guid trashId, Guid space) =>
        Run("""
            WITH RECURSIVE path(id, parent_id, name, depth) AS (
                SELECT id, parent_id, name, 0 FROM elements WHERE id=$1
                UNION ALL SELECT e.id, e.parent_id, e.name, p.depth+1 FROM elements e JOIN path p ON e.id=p.parent_id)
            INSERT INTO trash_entries (id, space_id, original_parent_id, original_path, deleted_at, deleted_by)
            SELECT $5, $2, (SELECT parent_id FROM elements WHERE id=$1),
                COALESCE((SELECT '/' || string_agg(name, '/' ORDER BY depth DESC) FROM path WHERE depth>0), '/'), $3, $4
            """, sourceId, space, clock.GetUtcNow(), account.Id, trashId);

    private async Task<Element> ReplaceContent(Account account, Guid id, Guid space, string key, long size, string contentType)
    {
        var snapshot = Guid.NewGuid();
        await RecordDeletion(account, id, snapshot, space);
        // Keep the file's identity, name and shares; the old bytes become a separate, unshared trashed element.
        await Run("""
            WITH previous AS MATERIALIZED (SELECT * FROM elements WHERE id=$1),
            updated AS (UPDATE elements SET object_key=$3, size=$4, content_type=$5, added_at=$6, added_by=$7
                WHERE id=$1 RETURNING id)
            INSERT INTO elements (id, space_id, kind, name, size, content_type, object_key, added_at, added_by, trash_id, thumbnail_state, thumbnail_key)
            SELECT $2, p.space_id, p.kind, p.name, p.size, p.content_type, p.object_key, p.added_at, p.added_by, $2,
                CASE WHEN p.thumbnail_state='pending' THEN NULL ELSE p.thumbnail_state END,
                CASE WHEN p.thumbnail_state='available' THEN p.thumbnail_key ELSE NULL END
            FROM previous p CROSS JOIN updated
            """, id, snapshot, key, size, contentType, clock.GetUtcNow(), account.Id);
        return await Get(id);
    }

    public async Task<List<TrashedElement>> Trash(Account account, string space)
    {
        var spaceId = await Destination(account, null, space);
        return await Query($"""
            SELECT {ElementColumns},
                t.original_parent_id, t.original_path, t.deleted_at, d.id, d.username
            FROM trash_entries t JOIN elements e ON e.id=t.id LEFT JOIN accounts a ON a.id=e.added_by
            LEFT JOIN accounts d ON d.id=t.deleted_by WHERE t.space_id=$1 ORDER BY t.deleted_at, t.id
            """, r => new TrashedElement(r.GetGuid(0), ReadElement(r), r.IsDBNull(10) ? null : r.GetGuid(10),
                r.GetString(11), r.GetFieldValue<DateTimeOffset>(12), r.IsDBNull(13) ? null : new AccountSummary(r.GetGuid(13), r.GetString(14))), spaceId);
    }

    private async Task<(Guid Space, Guid? Parent)> TrashAccess(Account account, Guid id)
    {
        var rows = await Query("""
            SELECT t.space_id, t.original_parent_id FROM trash_entries t JOIN spaces s ON s.id=t.space_id
            WHERE t.id=$1 AND (s.owner_id=$2 OR s.owner_id IS NULL)
            """, r => (r.GetGuid(0), r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1)), id, account.Id);
        if (rows.Count == 0) throw new StorageError(404, "trash_not_found");
        return rows[0];
    }

    public async Task<Element> Restore(Account account, Guid id)
    {
        await LockSpaces(account, moving: true, elementId: id);
        var entry = await TrashAccess(account, id);
        if (await Scalar("SELECT 1 FROM trash_entries WHERE id=$1 AND deleted_at>$2", id, clock.GetUtcNow().AddDays(-30)) is null)
            throw new StorageError(404, "trash_not_found");
        var parent = entry.Parent is not null && await Scalar("SELECT 1 FROM elements WHERE id=$1 AND space_id=$2 AND trash_id IS NULL", entry.Parent, entry.Space) is not null
            ? entry.Parent : null;
        var element = await Get(id);
        for (var number = 0; ; number++)
        {
            var name = Numbered(element.Name, number);
            if (await Scalar("SELECT 1 FROM elements WHERE space_id=$1 AND parent_id IS NOT DISTINCT FROM $2 AND upper(name)=upper($3) AND trash_id IS NULL", entry.Space, parent, name) is not null)
                continue;
            await Run("UPDATE elements SET name=$2, parent_id=$3 WHERE id=$1", id, name, parent);
            await Run("UPDATE elements SET trash_id=NULL WHERE trash_id=$1", id);
            await Run("DELETE FROM trash_entries WHERE id=$1", id);
            return await Get(id);
        }
    }

    public async Task Purge(Account account, Guid id)
    {
        await LockSpaces(account, moving: true, elementId: id);
        await TrashAccess(account, id);
        // Cascades remove the entire snapshot; database triggers enqueue every object in this transaction.
        await Run("DELETE FROM trash_entries WHERE id=$1", id);
    }

    public async Task EmptyTrash(Account account, string space)
    {
        await LockSpaces(account, moving: true, rootSpace: space);
        var spaceId = await Destination(account, null, space);
        await Run("DELETE FROM trash_entries WHERE space_id=$1", spaceId);
    }
}
