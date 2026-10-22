namespace Shelf.Api.Storage;

/// <summary>Declares an upload into a folder, or the selected space's root (personal by default). OnConflict "keepBoth" renames; "replace" preserves the file and trashes its previous content. Alternatively, ReplaceFileId replaces exactly that file, without Name, ParentId, Space or OnConflict, even when its parent is hidden.</summary>
public sealed record UploadRequest(string? Name, long? Size, string? ContentType, Guid? ParentId = null, string? OnConflict = null, string? Space = null, Guid? ReplaceFileId = null);
/// <summary>Signed URLs for the parts still to send. Part n carries bytes [(n-1)·PartSize, n·PartSize) of the file.</summary>
public sealed record UploadTicket(Guid Id, string Method, IReadOnlyDictionary<string, string> Headers, long PartSize, IReadOnlyList<UploadPart> Parts, DateTimeOffset ExpiresAt);
public sealed record UploadPart(int Number, string Url);
public sealed record FolderRequest(string? Name, Guid? ParentId = null, string? Space = null);
public sealed record RenameRequest(string? Name);
/// <summary>Moves into a visible folder or a space root. Without Space or ParentId, stays in the current space. Personal elements may move to common, never the reverse.</summary>
public sealed record MoveRequest(Guid? ParentId = null, string? Space = null);
/// <summary>Copies into a writable folder or a space root (personal by default), recursively and without copying shares. Name conflicts are renamed automatically.</summary>
public sealed record CopyRequest(Guid? ParentId = null, string? Space = null);
public sealed record AccountSummary(Guid Id, string Username);
/// <summary>A file or a folder; Size and ContentType are null for folders.</summary>
public sealed record Element(Guid Id, string Kind, string Name, Guid? ParentId, long? Size, string? ContentType, DateTimeOffset AddedAt, AccountSummary? AddedBy);

public sealed class StorageError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Sets a share's level to "read" or "write". Only the owner manages shares; recipients cannot rename, move or delete a shared root.</summary>
public sealed record ShareRequest(string? Level);
/// <summary>A direct or inherited grant; ElementId identifies the root on which it was granted.</summary>
public sealed record Share(Guid ElementId, AccountSummary Recipient, string Level);
public sealed record SharedElement(Element Element, AccountSummary Owner, string Level);

/// <summary>A deleted subtree. OriginalParentId and OriginalPath record its location at deletion.</summary>
public sealed record TrashedElement(Guid Id, Element Element, Guid? OriginalParentId, string OriginalPath, DateTimeOffset DeletedAt, AccountSummary? DeletedBy);
