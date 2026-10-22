using Npgsql;
using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

/// <summary>Storage operations in one PostgreSQL transaction, without the account advisory lock.</summary>
public sealed class StorageTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, ObjectStore store, TimeProvider clock, CancellationToken cancellation)
{
    private const string ElementColumns = "e.id, e.kind, e.name, e.parent_id, e.size, e.content_type, e.added_at, a.id, a.username FROM elements e JOIN spaces s ON s.id=e.space_id LEFT JOIN accounts a ON a.id=e.added_by";
    private static Element ReadElement(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : new AccountSummary(r.GetGuid(7), r.GetString(8)));
    private static readonly Dictionary<string, string> Sorts = new() { ["name"] = "lower(e.name) {0}, e.name {0}", ["addedAt"] = "e.added_at {0}", ["size"] = "e.size {0}" };

    /// <summary>Content of a folder, or of the root of the personal space when folderId is null. Folders come before files.</summary>
    public async Task<List<Element>> Content(Account account, Guid? folderId, string? sort, string? order)
    {
        if (!Sorts.TryGetValue(sort ?? "name", out var key)) throw new StorageError(400, "invalid_sort");
        if ((order ??= "asc") is not ("asc" or "desc")) throw new StorageError(400, "invalid_order");
        var space = await Destination(account, folderId);
        return await Query($"SELECT {ElementColumns} WHERE e.space_id=$1 AND e.parent_id IS NOT DISTINCT FROM $2 ORDER BY e.kind='file', {string.Format(key, order)}, lower(e.name), e.id",
            ReadElement, space, folderId);
    }

    public async Task<Element> CreateFolder(Account account, FolderRequest input)
    {
        var name = Name(input.Name);
        var space = await Destination(account, input.ParentId);
        var id = Guid.NewGuid();
        await Run("INSERT INTO elements (id, space_id, parent_id, kind, name, added_at, added_by) VALUES ($1,$2,$3,'folder',$4,$5,$6)",
            id, space, input.ParentId, name, clock.GetUtcNow(), account.Id);
        return await Get(id);
    }

    /// <summary>Signed URLs are listed in batches: the client asks for the next missing parts after sending these.</summary>
    private const int MaxSignedParts = 1000;

    private sealed record Upload(Guid Id, Guid Space, string Name, long Size, string ContentType, string Key, string? MultipartId, long PartSize, DateTimeOffset CreatedAt, Guid? Parent, bool KeepBoth)
    {
        public int PartCount => MultipartId is null ? 1 : (int)((Size + PartSize - 1) / PartSize);
        public long ExpectedSize(int number) => number < PartCount ? PartSize : Size - (PartCount - 1) * PartSize;
    }

    public async Task<UploadTicket> Declare(Account account, UploadRequest input)
    {
        var name = Name(input.Name);
        if (input.Size is not >= 0) throw new StorageError(400, "invalid_size");
        var size = input.Size.Value;
        var contentType = ContentType(input.ContentType);
        if (input.OnConflict is not (null or "keepBoth")) throw new StorageError(400, "invalid_conflict_policy");
        var space = await Destination(account, input.ParentId);
        // Checked again at confirmation, since another element may take the name meanwhile.
        if (input.OnConflict is null && await Scalar("SELECT 1 FROM elements WHERE space_id=$1 AND parent_id IS NOT DISTINCT FROM $2 AND upper(name)=upper($3)", space, input.ParentId, name) is not null)
            throw new StorageError(409, "name_conflict");
        var key = ObjectStore.NewKey();
        // Files larger than one part go through an S3 multipart upload, which can resume part by part.
        var multipart = size > store.PartSize ? await store.StartMultipart(key, contentType, cancellation) : null;
        var upload = new Upload(Guid.NewGuid(), space, name, size, contentType, key, multipart, multipart is null ? size : store.MultipartPartSize(size), clock.GetUtcNow(), input.ParentId, input.OnConflict is not null);
        await Run("INSERT INTO uploads (id, space_id, account_id, name, size, content_type, object_key, multipart_id, part_size, created_at, parent_id, on_conflict) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)",
            upload.Id, space, account.Id, name, size, contentType, key, (object?)multipart ?? DBNull.Value, upload.PartSize, upload.CreatedAt, input.ParentId, input.OnConflict);
        return Ticket(upload, Enumerable.Range(1, upload.PartCount));
    }

    /// <summary>New signed URLs for the parts not yet received with their expected size, to resume an interrupted upload.</summary>
    public async Task<UploadTicket> Resume(Account account, Guid uploadId)
    {
        var upload = await PendingUpload(account, uploadId);
        if (upload.MultipartId is null)
            return Ticket(upload, await store.Size(upload.Key, cancellation) == upload.Size ? [] : [1]);
        var parts = await store.Parts(upload.Key, upload.MultipartId, cancellation);
        return Ticket(upload, parts is null ? [] : Enumerable.Range(1, upload.PartCount).Where(n => parts.GetValueOrDefault(n)?.Size != upload.ExpectedSize(n)));
    }

    public async Task<Element> Complete(Account account, Guid uploadId)
    {
        var upload = await PendingUpload(account, uploadId);
        // Without its multipart upload, the object was already assembled by an earlier confirmation that did not commit.
        if (upload.MultipartId is not null && await store.Parts(upload.Key, upload.MultipartId, cancellation) is { } parts)
        {
            var numbers = Enumerable.Range(1, upload.PartCount).ToList();
            if (numbers.Any(n => !parts.ContainsKey(n))) throw new StorageError(409, "upload_incomplete");
            if (numbers.Any(n => parts[n].Size != upload.ExpectedSize(n))) throw new StorageError(409, "size_mismatch");
            await store.Assemble(upload.Key, upload.MultipartId, numbers.Select(n => parts[n]), cancellation);
        }
        var size = await store.Size(upload.Key, cancellation);
        if (size is null) throw new StorageError(409, "upload_incomplete");
        if (size != upload.Size) throw new StorageError(409, "size_mismatch");
        var id = Guid.NewGuid();
        await Run("DELETE FROM uploads WHERE id=$1", uploadId);
        // The unique name index decides between concurrent uploads: a taken name inserts nothing, then "keep both" tries the next number.
        for (var number = 0; ; number++)
        {
            if (await Scalar("""
                INSERT INTO elements (id, space_id, parent_id, kind, name, size, content_type, object_key, added_at, added_by)
                VALUES ($1,$2,$3,'file',$4,$5,$6,$7,$8,$9) ON CONFLICT (space_id, parent_id, upper(name)) DO NOTHING RETURNING id
                """, id, upload.Space, upload.Parent, Numbered(upload.Name, number), upload.Size, upload.ContentType, upload.Key, clock.GetUtcNow(), account.Id) is not null)
                break;
            if (!upload.KeepBoth) throw new StorageError(409, "name_conflict");
        }
        return await Get(id);
    }

    /// <summary>Purges one expired upload with its parts and object; false when none is left.</summary>
    // Uploads that lost their account or destination wait for their expiry too: a signed URL may stay valid until then.
    public async Task<bool> PurgeExpiredUpload()
    {
        var uploads = await Query("SELECT id, object_key, multipart_id FROM uploads WHERE created_at <= $1 LIMIT 1 FOR UPDATE SKIP LOCKED",
            r => (Id: r.GetGuid(0), Key: r.GetString(1), MultipartId: r.IsDBNull(2) ? null : r.GetString(2)), clock.GetUtcNow() - store.UploadExpiry);
        if (uploads.Count == 0) return false;
        await store.Discard(uploads[0].Key, uploads[0].MultipartId, cancellation);
        await Run("DELETE FROM uploads WHERE id=$1", uploads[0].Id);
        return true;
    }

    private async Task<Upload> PendingUpload(Account account, Guid uploadId)
    {
        // An expired upload or one without a destination is gone for the client before cleanup.
        var uploads = await Query("SELECT id, space_id, name, size, content_type, object_key, multipart_id, part_size, created_at, parent_id, on_conflict FROM uploads WHERE id=$1 AND account_id=$2 AND space_id IS NOT NULL AND created_at > $3 FOR UPDATE",
            r => new Upload(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetGuid(9), !r.IsDBNull(10)),
            uploadId, account.Id, clock.GetUtcNow() - store.UploadExpiry);
        if (uploads.Count == 0) throw new StorageError(404, "upload_not_found");
        if (await Destination(account, uploads[0].Parent) != uploads[0].Space) throw new StorageError(404, "folder_not_found");
        return uploads[0];
    }

    private UploadTicket Ticket(Upload upload, IEnumerable<int> missing)
    {
        // No URL outlives the upload, so nothing can be sent after the cleanup task purged it.
        var expires = store.UrlExpiry(upload.CreatedAt + store.UploadExpiry - clock.GetUtcNow());
        var parts = missing.Take(MaxSignedParts).Select(number => new UploadPart(number, upload.MultipartId is null
            ? store.SignedUpload(upload.Key, upload.ContentType, expires)
            : store.SignedPart(upload.Key, upload.MultipartId, number, expires))).ToList();
        // A single PUT signs the content type; multipart parts inherit it from the upload.
        var headers = upload.MultipartId is null ? new Dictionary<string, string> { ["Content-Type"] = upload.ContentType } : new Dictionary<string, string>();
        return new(upload.Id, "PUT", headers, upload.PartSize, parts, new DateTimeOffset(expires));
    }

    public async Task<SignedUrl> Download(Account account, Guid fileId)
    {
        var files = await Query("SELECT e.name, e.object_key FROM elements e JOIN spaces s ON s.id=e.space_id WHERE e.id=$1 AND e.kind='file' AND s.owner_id=$2",
            r => (Name: r.GetString(0), Key: r.GetString(1)), fileId, account.Id);
        if (files.Count == 0) throw new StorageError(404, "element_not_found");
        return store.SignedDownload(files[0].Key, files[0].Name);
    }

    /// <summary>Renaming changes metadata only; the S3 object key does not depend on the name.</summary>
    public async Task<Element> Rename(Account account, Guid elementId, RenameRequest input)
    {
        var name = Name(input.Name);
        if (await Scalar("UPDATE elements e SET name=$3 FROM spaces s WHERE s.id=e.space_id AND e.id=$1 AND s.owner_id=$2 RETURNING e.id", elementId, account.Id, name) is null)
            throw new StorageError(404, "element_not_found");
        return await Get(elementId);
    }

    /// <summary>Moving changes metadata only; the S3 object key does not depend on the folder.</summary>
    public async Task<Element> Move(Account account, Guid elementId, MoveRequest input)
    {
        if (await Scalar("SELECT e.space_id FROM elements e JOIN spaces s ON s.id=e.space_id WHERE e.id=$1 AND s.owner_id=$2", elementId, account.Id) is not Guid space)
            throw new StorageError(404, "element_not_found");
        // Moves of a space run one at a time, so two concurrent moves cannot each pass the cycle check and form a cycle together.
        // NO KEY UPDATE leaves the foreign key checks of uploads and folder creations in the space unblocked.
        await Run("SELECT 1 FROM spaces WHERE id=$1 FOR NO KEY UPDATE", space);
        if (await Destination(account, input.ParentId) != space) throw new StorageError(404, "folder_not_found");
        if (input.ParentId is not null && await Scalar("""
            WITH RECURSIVE ancestors(id, parent_id) AS (
                SELECT id, parent_id FROM elements WHERE id=$1
                UNION ALL SELECT e.id, e.parent_id FROM elements e JOIN ancestors a ON e.id=a.parent_id)
            SELECT 1 FROM ancestors WHERE id=$2
            """, input.ParentId, elementId) is not null)
            throw new StorageError(409, "folder_cycle");
        await Run("UPDATE elements SET parent_id=$2 WHERE id=$1", elementId, input.ParentId);
        return await Get(elementId);
    }

    /// <summary>Space of a visible folder, or the personal space when folderId is null.</summary>
    private async Task<Guid> Destination(Account account, Guid? folderId) => folderId is null
        ? (Guid)(await Scalar("SELECT id FROM spaces WHERE owner_id=$1", account.Id))!
        : await Scalar("SELECT e.space_id FROM elements e JOIN spaces s ON s.id=e.space_id WHERE e.id=$1 AND e.kind='folder' AND s.owner_id=$2", folderId, account.Id) as Guid?
            ?? throw new StorageError(404, "folder_not_found");

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
