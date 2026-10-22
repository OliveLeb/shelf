namespace Shelf.Api.Storage;

/// <summary>Declares an upload into a folder, or the selected space's root (personal by default). OnConflict "keepBoth" renames on a name conflict.</summary>
public sealed record UploadRequest(string? Name, long? Size, string? ContentType, Guid? ParentId = null, string? OnConflict = null, string? Space = null);
public sealed record UploadTicket(Guid Id, string Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);
public sealed record FolderRequest(string? Name, Guid? ParentId = null, string? Space = null);
public sealed record RenameRequest(string? Name);
/// <summary>Moves into a visible folder or a space root. Without Space or ParentId, stays in the current space. Personal elements may move to common, never the reverse.</summary>
public sealed record MoveRequest(Guid? ParentId = null, string? Space = null);
public sealed record AccountSummary(Guid Id, string Username);
/// <summary>A file or a folder; Size and ContentType are null for folders.</summary>
public sealed record Element(Guid Id, string Kind, string Name, Guid? ParentId, long? Size, string? ContentType, DateTimeOffset AddedAt, AccountSummary? AddedBy);

public sealed class StorageError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
