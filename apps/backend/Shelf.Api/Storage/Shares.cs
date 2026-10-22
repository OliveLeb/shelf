using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    private const string Ancestors = """
        WITH RECURSIVE ancestors(id, parent_id) AS (
            SELECT id, parent_id FROM elements WHERE id=$1
            UNION ALL SELECT e.id, e.parent_id FROM elements e JOIN ancestors a ON e.id=a.parent_id)
        """;
    private sealed record ElementAccess(Guid Space, string Kind, Guid? Owner, bool CanWrite, bool CanManage);

    /// <summary>Resolve visibility before checking a requested operation: invisible elements always return 404.
    /// A grant on any ancestor covers the element, so new descendants inherit access without copying grants.</summary>
    private async Task<ElementAccess> Access(Account account, Guid id, string missing = "element_not_found")
    {
        var rows = await Query(Ancestors + """
            SELECT e.space_id, e.kind, s.owner_id,
                (s.owner_id IS NULL OR s.owner_id=$2 OR EXISTS (
                    SELECT 1 FROM shares sh JOIN ancestors a ON a.id=sh.element_id
                    WHERE sh.recipient_id=$2 AND sh.level='write')),
                (s.owner_id IS NULL OR s.owner_id=$2 OR NOT EXISTS (
                    SELECT 1 FROM shares sh WHERE sh.element_id=e.id))
            FROM elements e JOIN spaces s ON s.id=e.space_id WHERE e.id=$1
            AND e.trash_id IS NULL
            AND (s.owner_id IS NULL OR s.owner_id=$2 OR EXISTS (
                SELECT 1 FROM shares sh JOIN ancestors a ON a.id=sh.element_id WHERE sh.recipient_id=$2))
            """, r => new ElementAccess(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3), r.GetBoolean(4)), id, account.Id);
        return rows.SingleOrDefault() ?? throw new StorageError(404, missing);
    }

    private async Task RequireMutableSubtree(Account account, Guid id, ElementAccess access)
    {
        if (!access.CanWrite || !access.CanManage) throw new StorageError(403, "write_forbidden");
        if (access.Owner is null || access.Owner == account.Id) return;
        // A container operation also moves or deletes its descendants, including any protected roots.
        if (await Scalar("""
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM elements WHERE id=$1
                UNION ALL SELECT e.id FROM elements e JOIN subtree p ON e.parent_id=p.id)
            SELECT 1 FROM shares WHERE element_id IN (SELECT id FROM subtree) LIMIT 1
            """, id) is not null)
            throw new StorageError(403, "write_forbidden");
    }

    private async Task RequireOwner(Account account, Guid id)
    {
        var access = await Access(account, id);
        if (access.Owner != account.Id) throw new StorageError(403, "sharing_forbidden");
    }

    public async Task<Share> SetShare(Account account, Guid id, Guid recipientId, ShareRequest input)
    {
        await LockSpaces(account, moving: true, elementId: id);
        await RequireOwner(account, id);
        if (input.Level is not ("read" or "write")) throw new StorageError(400, "invalid_share_level");
        if (recipientId == account.Id) throw new StorageError(400, "invalid_recipient");
        var recipients = await Query("""
            INSERT INTO shares (element_id, recipient_id, level)
            SELECT $1, id, $3 FROM accounts WHERE id=$2 AND is_active
            ON CONFLICT (element_id, recipient_id) DO UPDATE SET level=EXCLUDED.level
            RETURNING recipient_id, (SELECT username FROM accounts WHERE id=$2)
            """, r => new AccountSummary(r.GetGuid(0), r.GetString(1)), id, recipientId, input.Level);
        if (recipients.Count == 0) throw new StorageError(400, "invalid_recipient");
        return new Share(id, recipients[0], input.Level);
    }

    public async Task RemoveShare(Account account, Guid id, Guid recipientId)
    {
        await LockSpaces(account, moving: true, elementId: id);
        await RequireOwner(account, id);
        await Run("DELETE FROM shares WHERE element_id=$1 AND recipient_id=$2", id, recipientId);
    }

    public async Task<List<Share>> Shares(Account account, Guid id)
    {
        await RequireOwner(account, id);
        return await Query(Ancestors + """
            SELECT sh.element_id, a.id, a.username, sh.level FROM shares sh
            JOIN accounts a ON a.id=sh.recipient_id
            WHERE sh.element_id IN (SELECT id FROM ancestors) ORDER BY lower(a.username), a.id, sh.element_id
            """, r => new Share(r.GetGuid(0), new AccountSummary(r.GetGuid(1), r.GetString(2)), r.GetString(3)), id);
    }

    public Task<List<SharedElement>> ReceivedShares(Account account) => Query("""
        SELECT e.id, e.kind, e.name, e.parent_id, e.size, e.content_type, e.added_at, a.id, a.username,
            owner.id, owner.username, CASE WHEN EXISTS (
                WITH RECURSIVE ancestors(id, parent_id) AS (
                    SELECT e.id, e.parent_id
                    UNION ALL SELECT p.id, p.parent_id FROM elements p JOIN ancestors a ON p.id=a.parent_id)
                SELECT 1 FROM shares inherited JOIN ancestors a ON a.id=inherited.element_id
                WHERE inherited.recipient_id=$1 AND inherited.level='write') THEN 'write' ELSE 'read' END
        FROM elements e JOIN spaces s ON s.id=e.space_id LEFT JOIN accounts a ON a.id=e.added_by
        JOIN shares sh ON sh.element_id=e.id JOIN accounts owner ON owner.id=s.owner_id
        WHERE sh.recipient_id=$1 AND e.trash_id IS NULL ORDER BY lower(e.name), e.id
        """, r => new SharedElement(ReadElement(r), new AccountSummary(r.GetGuid(9), r.GetString(10)), r.GetString(11)), account.Id);
}
