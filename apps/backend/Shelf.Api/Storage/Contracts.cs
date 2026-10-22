namespace Shelf.Api.Storage;

/// <summary>Declares an upload into a folder (root of the personal space when ParentId is null). OnConflict "keepBoth" renames on a name conflict.</summary>
public sealed record UploadRequest(string? Name, long? Size, string? ContentType, Guid? ParentId = null, string? OnConflict = null);
/// <summary>Signed URLs for the parts still to send. Part n carries bytes [(n-1)·PartSize, n·PartSize) of the file.</summary>
public sealed record UploadTicket(Guid Id, string Method, IReadOnlyDictionary<string, string> Headers, long PartSize, IReadOnlyList<UploadPart> Parts, DateTimeOffset ExpiresAt);
public sealed record UploadPart(int Number, string Url);
public sealed record FolderRequest(string? Name, Guid? ParentId = null);
public sealed record RenameRequest(string? Name);
/// <summary>Moves an element into a folder of the same space, or to its root when ParentId is null.</summary>
public sealed record MoveRequest(Guid? ParentId);
public sealed record AccountSummary(Guid Id, string Username);
/// <summary>A file or a folder; Size and ContentType are null for folders.</summary>
public sealed record Element(Guid Id, string Kind, string Name, Guid? ParentId, long? Size, string? ContentType, DateTimeOffset AddedAt, AccountSummary? AddedBy);

public sealed class StorageError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
