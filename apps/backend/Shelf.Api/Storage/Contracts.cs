namespace Shelf.Api.Storage;

public sealed record UploadRequest(string? Name, long? Size, string? ContentType);
public sealed record UploadTicket(Guid Id, string Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);
public sealed record AccountSummary(Guid Id, string Username);
public sealed record FileElement(Guid Id, string Name, long Size, string ContentType, DateTimeOffset AddedAt, AccountSummary? AddedBy);

public sealed class StorageError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
