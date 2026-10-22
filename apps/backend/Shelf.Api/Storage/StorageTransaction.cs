using Npgsql;
using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

/// <summary>Storage operations in one PostgreSQL transaction, without the account advisory lock.</summary>
public sealed partial class StorageTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, ObjectStore store, TimeProvider clock, CancellationToken cancellation)
{
    private const string ElementColumns = "e.id, e.kind, e.name, e.parent_id, e.size, e.content_type, e.added_at, a.id, a.username FROM elements e JOIN spaces s ON s.id=e.space_id LEFT JOIN accounts a ON a.id=e.added_by";
    private static Element ReadElement(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : new AccountSummary(r.GetGuid(7), r.GetString(8)));
    private static readonly Dictionary<string, string> Sorts = new() { ["name"] = "lower(e.name) {0}, e.name {0}", ["addedAt"] = "e.added_at {0}", ["size"] = "e.size {0}" };

    /// <summary>Content of a visible folder, or of the selected space's root. Folders come before files.</summary>
    public async Task<List<Element>> Content(Account account, Guid? folderId, string? sort, string? order, string? space = null)
    {
        if (!Sorts.TryGetValue(sort ?? "name", out var key)) throw new StorageError(400, "invalid_sort");
        if ((order ??= "asc") is not ("asc" or "desc")) throw new StorageError(400, "invalid_order");
        var destination = await Destination(account, folderId, space, writable: false);
        return await Query($"SELECT {ElementColumns} WHERE e.space_id=$1 AND e.parent_id IS NOT DISTINCT FROM $2 AND e.trash_id IS NULL ORDER BY e.kind='file', {string.Format(key, order)}, lower(e.name), e.id",
            ReadElement, destination, folderId);
    }

    public async Task<Element> CreateFolder(Account account, FolderRequest input)
    {
        var name = Name(input.Name);
        await LockSpaces(account);
        var space = await Destination(account, input.ParentId, input.Space);
        var id = Guid.NewGuid();
        await Run("INSERT INTO elements (id, space_id, parent_id, kind, name, added_at, added_by) VALUES ($1,$2,$3,'folder',$4,$5,$6)",
            id, space, input.ParentId, name, clock.GetUtcNow(), account.Id);
        return await Get(id);
    }

    public async Task<UploadTicket> Declare(Account account, UploadRequest input)
    {
        var name = Name(input.Name);
        if (input.Size is not >= 0) throw new StorageError(400, "invalid_size");
        var contentType = ContentType(input.ContentType);
        if (input.OnConflict is not (null or "keepBoth" or "replace")) throw new StorageError(400, "invalid_conflict_policy");
        await LockSpaces(account);
        var space = await Destination(account, input.ParentId, input.Space);
        // Checked again at confirmation, since another element may take the name meanwhile.
        if (input.OnConflict is null && await Scalar("SELECT 1 FROM elements WHERE space_id=$1 AND parent_id IS NOT DISTINCT FROM $2 AND upper(name)=upper($3) AND trash_id IS NULL", space, input.ParentId, name) is not null)
            throw new StorageError(409, "name_conflict");
        if (input.OnConflict == "replace" && await Scalar("SELECT 1 FROM elements WHERE space_id=$1 AND parent_id IS NOT DISTINCT FROM $2 AND upper(name)=upper($3) AND trash_id IS NULL AND kind='folder'", space, input.ParentId, name) is not null)
            throw new StorageError(409, "name_conflict");
        var id = Guid.NewGuid();
        var key = ObjectStore.NewKey();
        await Run("INSERT INTO uploads (id, space_id, account_id, name, size, content_type, object_key, created_at, parent_id, on_conflict) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
            id, space, account.Id, name, input.Size.Value, contentType, key, clock.GetUtcNow(), input.ParentId, input.OnConflict);
        var signed = store.SignedUpload(key, contentType);
        return new(id, signed.Url, "PUT", new Dictionary<string, string> { ["Content-Type"] = contentType }, signed.ExpiresAt);
    }

    public async Task<Element> Complete(Account account, Guid uploadId)
    {
        // Serialize confirmation with replacement, trash, restore and moves before resolving the current name.
        await LockSpaces(account, moving: true);
        var uploads = await Query("SELECT space_id, name, size, content_type, object_key, parent_id, on_conflict FROM uploads WHERE id=$1 AND account_id=$2 FOR UPDATE",
            r => (Space: r.GetGuid(0), Name: r.GetString(1), Size: r.GetInt64(2), ContentType: r.GetString(3), Key: r.GetString(4),
                Parent: r.IsDBNull(5) ? (Guid?)null : r.GetGuid(5), Policy: r.IsDBNull(6) ? null : r.GetString(6)), uploadId, account.Id);
        if (uploads.Count == 0) throw new StorageError(404, "upload_not_found");
        var upload = uploads[0];
        // The destination may have moved since declaration; recheck it under the space locks.
        if (upload.Parent is not null && await Destination(account, upload.Parent) != upload.Space)
            throw new StorageError(404, "folder_not_found");
        var size = await store.Size(upload.Key, cancellation);
        if (size is null) throw new StorageError(409, "upload_incomplete");
        if (size != upload.Size) throw new StorageError(409, "size_mismatch");
        if (upload.Policy == "replace")
        {
            var existing = await Query("SELECT id, kind FROM elements WHERE space_id=$1 AND parent_id IS NOT DISTINCT FROM $2 AND upper(name)=upper($3) AND trash_id IS NULL",
                r => (Id: r.GetGuid(0), Kind: r.GetString(1)), upload.Space, upload.Parent, upload.Name);
            if (existing.Count > 0)
            {
                if (existing[0].Kind != "file") throw new StorageError(409, "name_conflict");
                var replaced = await ReplaceContent(account, existing[0].Id, upload.Space, upload.Key, upload.Size, upload.ContentType);
                await Run("DELETE FROM uploads WHERE id=$1", uploadId);
                return replaced;
            }
        }
        var id = Guid.NewGuid();
        // The unique name index decides between concurrent uploads: a taken name inserts nothing, then "keep both" tries the next number.
        for (var number = 0; ; number++)
        {
            if (await Scalar("""
                INSERT INTO elements (id, space_id, parent_id, kind, name, size, content_type, object_key, added_at, added_by)
                VALUES ($1,$2,$3,'file',$4,$5,$6,$7,$8,$9) ON CONFLICT (space_id, parent_id, upper(name)) WHERE trash_id IS NULL DO NOTHING RETURNING id
                """, id, upload.Space, upload.Parent, Numbered(upload.Name, number), upload.Size, upload.ContentType, upload.Key, clock.GetUtcNow(), account.Id) is not null)
                break;
            if (upload.Policy != "keepBoth") throw new StorageError(409, "name_conflict");
        }
        // Transfer the key before deleting the upload so the outbox does not delete confirmed content.
        await Run("DELETE FROM uploads WHERE id=$1", uploadId);
        return await Get(id);
    }

    public async Task<SignedUrl> Download(Account account, Guid fileId)
    {
        var access = await Access(account, fileId);
        if (access.Kind != "file") throw new StorageError(404, "element_not_found");
        var files = await Query("SELECT name, object_key FROM elements WHERE id=$1",
            r => (Name: r.GetString(0), Key: r.GetString(1)), fileId);
        if (files.Count == 0) throw new StorageError(404, "element_not_found");
        return store.SignedDownload(files[0].Key, files[0].Name);
    }

    /// <summary>Renaming changes metadata only; the S3 object key does not depend on the name.</summary>
    public async Task<Element> Rename(Account account, Guid elementId, RenameRequest input)
    {
        var name = Name(input.Name);
        await LockSpaces(account);
        var access = await Access(account, elementId);
        if (!access.CanWrite) throw new StorageError(403, "write_forbidden");
        await Run("UPDATE elements SET name=$2 WHERE id=$1", elementId, name);
        return await Get(elementId);
    }

    /// <summary>Moving changes metadata only; the S3 object key does not depend on the folder.</summary>
    public async Task<Element> Move(Account account, Guid elementId, MoveRequest input)
    {
        await LockSpaces(account, moving: true);
        var access = await Access(account, elementId);
        if (!access.CanWrite) throw new StorageError(403, "write_forbidden");
        var space = access.Space;
        var common = access.Owner is null;
        var destination = await Destination(account, input.ParentId, input.Space ?? (input.ParentId is null && common ? "common" : null));
        if (common && destination != space) throw new StorageError(403, "common_to_personal_forbidden");
        if (input.ParentId is not null && await Scalar("""
            WITH RECURSIVE ancestors(id, parent_id) AS (
                SELECT id, parent_id FROM elements WHERE id=$1
                UNION ALL SELECT e.id, e.parent_id FROM elements e JOIN ancestors a ON e.id=a.parent_id)
            SELECT 1 FROM ancestors WHERE id=$2
            """, input.ParentId, elementId) is not null)
            throw new StorageError(409, "folder_cycle");
        if (destination == space)
            await Run("UPDATE elements SET parent_id=$2 WHERE id=$1", elementId, input.ParentId);
        else
            await Run("""
                WITH RECURSIVE subtree(id) AS (
                    SELECT id FROM elements WHERE id=$1
                    UNION ALL SELECT e.id FROM elements e JOIN subtree p ON e.parent_id=p.id),
                moved AS (
                    UPDATE elements SET space_id=$2, parent_id=CASE WHEN id=$1 THEN $3::uuid ELSE parent_id END
                    WHERE id IN (SELECT id FROM subtree) RETURNING id),
                removed_shares AS (
                    DELETE FROM shares WHERE element_id IN (SELECT id FROM moved))
                UPDATE uploads SET space_id=$2 WHERE parent_id IN (SELECT id FROM moved)
                """, elementId, destination, input.ParentId);
        return await Get(elementId);
    }

    /// <summary>Writes share locks; moves exclude writes so a subtree cannot gain children during transfer.
    /// Lock both accessible spaces in UUID order to avoid deadlocks between personal-to-common transfers.
    /// NO KEY UPDATE lets foreign key checks proceed, and concurrent ordinary writes remain possible.</summary>
    private Task LockSpaces(Account account, bool moving = false) => Run($"SELECT id FROM spaces WHERE owner_id=$1 OR owner_id IS NULL ORDER BY id FOR {(moving ? "NO KEY UPDATE" : "SHARE")}", account.Id);

    /// <summary>Space of a visible folder, or the selected root (personal by default). An explicit space must match the folder.</summary>
    private async Task<Guid> Destination(Account account, Guid? folderId, string? space = null, bool writable = true)
    {
        if (space is not (null or "personal" or "common")) throw new StorageError(400, "invalid_space");
        if (folderId is null)
            return (Guid)(await Scalar(space == "common" ? "SELECT id FROM spaces WHERE owner_id IS NULL" : "SELECT id FROM spaces WHERE owner_id=$1",
                space == "common" ? [] : [account.Id]))!;
        var access = await Access(account, folderId.Value, "folder_not_found");
        if (access.Kind != "folder" || space == "common" && access.Owner is not null || space == "personal" && access.Owner is null)
            throw new StorageError(404, "folder_not_found");
        if (writable && !access.CanWrite) throw new StorageError(403, "write_forbidden");
        return access.Space;
    }

    private async Task<Element> Get(Guid id) => (await Query($"SELECT {ElementColumns} WHERE e.id=$1", ReadElement, id)).Single();

    /// <summary>"keep both" name: photo.jpg, then photo (1).jpg, photo (2).jpg…</summary>
    private static string Numbered(string name, int number)
    {
        if (number == 0) return name;
        var suffix = $" ({number})";
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        if (stem.Length + suffix.Length + extension.Length > 255)
        {
            if (suffix.Length + extension.Length >= 255) (stem, extension) = (name, "");
            stem = stem[..(255 - suffix.Length - extension.Length)];
            if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        }
        return stem + suffix + extension;
    }

    private static string Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value is "." or ".." || value.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new StorageError(400, "invalid_name");
        return value;
    }

    private static string ContentType(string? value)
    {
        if (value is null) return "application/octet-stream";
        if (value.Length is 0 or > 255 || !value.Contains('/') || value.Any(c => c is < '!' or > '~' && c != ' '))
            throw new StorageError(400, "invalid_content_type");
        return value;
    }

    private NpgsqlCommand Command(string sql, object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return command;
    }

    private async Task Run(string sql, params object?[] values)
    {
        await using var command = Command(sql, values);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private async Task<object?> Scalar(string sql, params object?[] values)
    {
        await using var command = Command(sql, values);
        return await command.ExecuteScalarAsync(cancellation);
    }

    private async Task<List<T>> Query<T>(string sql, Func<NpgsqlDataReader, T> read, params object?[] values)
    {
        await using var command = Command(sql, values);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellation)) rows.Add(read(reader));
        return rows;
    }
}
