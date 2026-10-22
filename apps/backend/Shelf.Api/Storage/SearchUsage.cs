using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    public Task<List<Element>> Search(Account account, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 255) throw new StorageError(400, "invalid_query");
        return Query($"""
            WITH RECURSIVE shared(id) AS (
                SELECT e.id FROM elements e JOIN shares sh ON sh.element_id=e.id
                WHERE sh.recipient_id=$1 AND e.trash_id IS NULL
                UNION
                SELECT e.id FROM elements e JOIN shared p ON e.parent_id=p.id WHERE e.trash_id IS NULL)
            SELECT {ElementColumns}
            WHERE e.trash_id IS NULL AND strpos(lower(e.name), lower($2)) > 0
                AND (s.owner_id=$1 OR s.owner_id IS NULL OR e.id IN (SELECT id FROM shared))
            ORDER BY lower(e.name), e.id
            """, ReadElement, account.Id, query);
    }

    public async Task<StorageUsage> Usage(Account account) => (await Query("""
        SELECT COALESCE(sum(e.size) FILTER (WHERE s.owner_id=$1), 0)::bigint,
            COALESCE(sum(e.size) FILTER (WHERE s.owner_id IS NULL), 0)::bigint
        FROM spaces s LEFT JOIN elements e ON e.space_id=s.id AND e.kind='file'
        WHERE s.owner_id=$1 OR s.owner_id IS NULL
        """, r => new StorageUsage(r.GetInt64(0), r.GetInt64(1)), account.Id)).Single();

    public Task<List<AccountStorageUsage>> AccountUsage(Account account)
    {
        if (!account.IsAdmin) throw new StorageError(403, "administrator_required");
        return Query("""
            SELECT a.id, a.username, COALESCE(sum(e.size), 0)::bigint
            FROM accounts a LEFT JOIN spaces s ON s.owner_id=a.id
            LEFT JOIN elements e ON e.space_id=s.id AND e.kind='file'
            GROUP BY a.id, a.username ORDER BY lower(a.username), a.id
            """, r => new AccountStorageUsage(new AccountSummary(r.GetGuid(0), r.GetString(1)), r.GetInt64(2)));
    }
}
