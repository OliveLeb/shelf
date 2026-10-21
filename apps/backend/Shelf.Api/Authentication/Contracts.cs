using System.Text.Json.Serialization;

namespace Shelf.Api.Authentication;

public sealed record Account(Guid Id, string Username, string Email, bool IsAdmin, bool IsActive,
    [property: JsonIgnore] string PasswordHash);
public sealed record SessionInfo(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, bool IsCurrent);
public sealed record InvitationInfo(Guid Id, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Revoked, bool Consumed);
public sealed record Registration(string? Token, string? Username, string? Password);
public sealed record Login(string? Identifier, string? Password, string? DeviceName);
public sealed record UsernameChange(string? Username);
public sealed record EmailChange(string? Email, string? CurrentPassword);
public sealed record PasswordChange(string? Password, string? CurrentPassword);
public sealed record PasswordConfirmation(string? CurrentPassword);
public sealed record InvitationRequest(string? Email);
public sealed record ResetPassword(string? Token, string? Password);
public sealed record LoginResult(Account Account, Guid SessionId, string Token, DateTimeOffset ExpiresAt);
public sealed record Actor(Account Account, Guid SessionId);

public sealed class AuthError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
