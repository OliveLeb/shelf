using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    public async Task<BrowseResult> Browse(Account account, string space, Guid? folderId, string? sort, string? order)
    {
        if (space is not ("personal" or "common")) throw new StorageError(400, "invalid_space");
        await LockSpaces(account, folderId: folderId, rootSpace: space);
        ElementContext context;
        if (folderId is Guid id)
        {
            var access = await Access(account, id, "folder_not_found");
            // Personal browse is the account's own space, never a received shared folder.
            if (access.Kind != "folder" || (space == "personal" ? access.Owner != account.Id : access.Owner is not null))
                throw new StorageError(404, "folder_not_found");
            context = await ComposeContext(account, id, access);
        }
        else
            context = new(new(space), null, [], new(false, true, false, false, false, false, false));

        var elements = await Content(account, folderId, sort, order, space);
        // These lists are exclusively owned or common: no per-entry access/subtree queries.
        var entries = elements.Select(element => new ElementEntry(element,
            Capabilities(element.Kind, canWrite: true, canManage: true, mutableSubtree: true, owner: space == "personal"))).ToList();
        return new(context, entries);
    }

    public async Task<ElementContext> Context(Account account, Guid id)
    {
        // SHARE conflicts with move/trash/share-revocation locks through response composition.
        await LockSpaces(account, elementId: id);
        var access = await Access(account, id);
        return await ComposeContext(account, id, access);
    }

    private async Task<ElementContext> ComposeContext(Account account, Guid id, ElementAccess access)
    {
        // Authorization precedes metadata reads. Filter inside SQL so hidden ancestor names
        // never enter the response, including when a directly shared root has a hidden parent.
        var ancestors = await Query("""
            WITH RECURSIVE path(id, parent_id, depth) AS (
                SELECT id, parent_id, 0 FROM elements WHERE id=$1
                UNION ALL SELECT e.id, e.parent_id, p.depth+1 FROM elements e JOIN path p ON e.id=p.parent_id)
            SELECT e.id, e.name FROM path p JOIN elements e ON e.id=p.id JOIN spaces s ON s.id=e.space_id
            WHERE p.depth>0 AND e.trash_id IS NULL AND (s.owner_id IS NULL OR s.owner_id=$2 OR EXISTS (
                SELECT 1 FROM shares sh JOIN path granted ON granted.id=sh.element_id
                WHERE sh.recipient_id=$2 AND granted.depth>=p.depth))
            ORDER BY p.depth DESC
            """, r => new ElementAncestor(r.GetGuid(0), r.GetString(1)), id, account.Id);
        var element = await Get(id);
        if (element.ParentId is Guid parent && !ancestors.Any(a => a.Id == parent))
            element = element with { ParentId = null };
        var kind = access.Owner is null ? "common" : access.Owner == account.Id ? "personal" : "shared";
        return new(new(kind), element, ancestors,
            Capabilities(element.Kind, access.CanWrite, access.CanManage, await MutableSubtree(account, id, access), access.Owner == account.Id));
    }

    private static ElementCapabilities Capabilities(string kind, bool canWrite, bool canManage, bool mutableSubtree, bool owner) =>
        new(kind == "file", kind == "folder" && canWrite, canWrite && canManage, mutableSubtree, mutableSubtree, owner, true);
}
