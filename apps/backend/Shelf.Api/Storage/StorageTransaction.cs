using Npgsql;
using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

/// <summary>Storage operations in one PostgreSQL transaction, without the account advisory lock.</summary>
public sealed class StorageTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, ObjectStore store, TimeProvider clock, CancellationToken cancellation)
{
    private const string FileColumns = "e.id, e.name, e.size, e.content_type, e.added_at, a.id, a.username FROM elements e JOIN spaces s ON s.id=e.space_id LEFT JOIN accounts a ON a.id=e.added_by";
    private static FileElement ReadFile(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : new AccountSummary(r.GetGuid(5), r.GetString(6)));

    public Task<List<FileElement>> PersonalRoot(Account account) => Query($"SELECT {FileColumns} WHERE s.owner_id=$1 ORDER BY e.name", ReadFile, account.Id);

    public async Task<UploadTicket> Declare(Account account, UploadRequest input)
    {
        var name = Name(input.Name);
        if (input.Size is not >= 0) throw new StorageError(400, "invalid_size");
        var contentType = ContentType(input.ContentType);
        var space = (Guid)(await Scalar("SELECT id FROM spaces WHERE owner_id=$1", account.Id))!;
        // Checked again by the unique index at confirmation, since another file may take the name meanwhile.
        if (await Scalar("SELECT 1 FROM elements WHERE space_id=$1 AND upper(name)=upper($2)", space, name) is not null)
            throw new StorageError(409, "name_conflict");
        var id = Guid.NewGuid();
        var key = ObjectStore.NewKey();
        await Run("INSERT INTO uploads VALUES ($1,$2,$3,$4,$5,$6,$7,$8)", id, space, account.Id, name, input.Size.Value, contentType, key, clock.GetUtcNow());
        var signed = store.SignedUpload(key, contentType);
        return new(id, signed.Url, "PUT", new Dictionary<string, string> { ["Content-Type"] = contentType }, signed.ExpiresAt);
    }

    public async Task<FileElement> Complete(Account account, Guid uploadId)
    {
        var uploads = await Query("SELECT space_id, name, size, content_type, object_key FROM uploads WHERE id=$1 AND account_id=$2 FOR UPDATE",
            r => (Space: r.GetGuid(0), Name: r.GetString(1), Size: r.GetInt64(2), ContentType: r.GetString(3), Key: r.GetString(4)), uploadId, account.Id);
        if (uploads.Count == 0) throw new StorageError(404, "upload_not_found");
        var upload = uploads[0];
        var size = await store.Size(upload.Key, cancellation);
        if (size is null) throw new StorageError(409, "upload_incomplete");
        if (size != upload.Size) throw new StorageError(409, "size_mismatch");
        var id = Guid.NewGuid();
        await Run("DELETE FROM uploads WHERE id=$1", uploadId);
        await Run("INSERT INTO elements VALUES ($1,$2,$3,$4,$5,$6,$7,$8)", id, upload.Space, upload.Name, upload.Size, upload.ContentType, upload.Key, clock.GetUtcNow(), account.Id);
        return (await Query($"SELECT {FileColumns} WHERE e.id=$1", ReadFile, id)).Single();
    }

    public async Task<SignedUrl> Download(Account account, Guid fileId)
    {
        var files = await Query("SELECT e.name, e.object_key FROM elements e JOIN spaces s ON s.id=e.space_id WHERE e.id=$1 AND s.owner_id=$2",
            r => (Name: r.GetString(0), Key: r.GetString(1)), fileId, account.Id);
        if (files.Count == 0) throw new StorageError(404, "element_not_found");
        return store.SignedDownload(files[0].Key, files[0].Name);
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

    private NpgsqlCommand Command(string sql, object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }

    private async Task Run(string sql, params object[] values)
    {
        await using var command = Command(sql, values);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var command = Command(sql, values);
        return await command.ExecuteScalarAsync(cancellation);
    }

    private async Task<List<T>> Query<T>(string sql, Func<NpgsqlDataReader, T> read, params object[] values)
    {
        await using var command = Command(sql, values);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellation)) rows.Add(read(reader));
        return rows;
    }
}
