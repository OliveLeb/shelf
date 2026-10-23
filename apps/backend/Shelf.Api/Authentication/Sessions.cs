namespace Shelf.Api.Authentication;

public sealed partial class AuthTransaction
{
    public async Task<LoginResult> Login(Login input)
    {
        if (input.Identifier is null || input.Identifier.Length > 320) throw new AuthError(401, "invalid_credentials");
        var account = await FindLogin(input.Identifier.Trim());
        var valid = Verify(account, input.Password);
        if (!valid || account is null || !account.IsActive) throw new AuthError(401, "invalid_credentials");
        if (input.DeviceName is { Length: > 200 }) throw new AuthError(400, "invalid_device_name");
        var token = Secret();
        var id = Guid.NewGuid();
        var expires = Now.AddDays(30);
        await Run("INSERT INTO sessions VALUES ($1,$2,$3,$4,$5,$5,$6)", id, account.Id, Fingerprint(token), input.DeviceName ?? "Unknown device", Now, expires);
        return new(account, id, token, expires);
    }
    public async Task<Actor> Authenticate(string? authorization, bool admin, bool accountMutation = true, bool updateActivity = true)
    {
        var seenAt = Now;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new AuthError(401, "authentication_required");
        var token = authorization[7..];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new AuthError(401, "authentication_required");
        var rows = await Query("SELECT id, account_id FROM sessions WHERE token_hash=$1 AND expires_at>$2", r => (Id: r.GetGuid(0), AccountId: r.GetGuid(1)), Fingerprint(token), Now);
        if (rows.Count == 0) throw new AuthError(401, "authentication_required");
        var session = rows[0];
        // Lock the account before touching its session, through the transaction's commit.
        // Deletion/disable either finishes first (deny access), or waits for this authorized operation.
        // Auth operations may mutate their own account: block storage's SHARE before locking the
        // session, without blocking foreign key checks from other accounts' storage operations.
        // Storage shares the lock; KEY SHARE would not block a disable, which only changes is_active.
        var account = (await Query($"SELECT {AccountColumns} FROM accounts WHERE id=$1 FOR {(accountMutation ? "NO KEY UPDATE" : "SHARE")}", ReadAccount, session.AccountId)).SingleOrDefault();
        if (account is null || !account.IsActive) throw new AuthError(401, "authentication_required");
        if (admin && !account.IsAdmin) throw new AuthError(403, "administrator_required");
        // Check the clock AFTER acquiring the session lock, not before a potentially long wait.
        // KEY SHARE permits activity updates and other storage operations, but prevents revocation.
        var expiry = await Query($"SELECT expires_at FROM sessions WHERE id=$1 FOR {(updateActivity ? "NO KEY UPDATE" : "KEY SHARE")}",
            r => r.GetFieldValue<DateTimeOffset>(0), session.Id);
        if (expiry.Count == 0 || expiry[0] <= Now) throw new AuthError(401, "authentication_required");
        if (updateActivity)
        {
            if (await Run("UPDATE sessions SET last_seen_at=GREATEST(last_seen_at,$2), expires_at=GREATEST(expires_at,$3) WHERE id=$1",
                session.Id, seenAt, seenAt.AddDays(30)) == 0)
                throw new AuthError(401, "authentication_required");
        }
        return new(account, session.Id);
    }
    public Task<List<SessionInfo>> Sessions(Actor actor) => Query("SELECT id, device_name, created_at, last_seen_at, expires_at FROM sessions WHERE account_id=$1 AND expires_at>$2 ORDER BY created_at", r => new SessionInfo(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetGuid(0) == actor.SessionId), actor.Account.Id, Now);
    public async Task RevokeSession(Actor actor, Guid id)
    {
        if (await Run("DELETE FROM sessions WHERE id=$1 AND account_id=$2", id, actor.Account.Id) == 0) throw new AuthError(404, "session_not_found");
    }
    public Task<int> RevokeAll(Guid accountId) => Run("DELETE FROM sessions WHERE account_id=$1", accountId);
    private Task<int> RevokeOthers(Actor actor) => Run("DELETE FROM sessions WHERE account_id=$1 AND id<>$2", actor.Account.Id, actor.SessionId);
}
