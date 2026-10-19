namespace Shelf.Api.Authentication;

public sealed partial class AuthTransaction
{
    public async Task<object> Invite(string? email, string origin)
    {
        var address = Email(email);
        if (await FindLogin(address) is not null) throw new AuthError(409, "email_already_registered");
        await Run("UPDATE invitations SET revoked=true WHERE upper(email)=upper($1) AND NOT consumed", address);
        var token = Secret();
        var id = Guid.NewGuid();
        var expires = Now.AddDays(7);
        await Run("INSERT INTO invitations (id,email,token_hash,created_at,expires_at) VALUES ($1,$2,$3,$4,$5)", id, address, Fingerprint(token), Now, expires);
        return new { id, email = address, expiresAt = expires, link = $"{origin}/accept-invitation#token={token}" };
    }
    public Task<List<InvitationInfo>> Invitations() => Query("SELECT id,email,created_at,expires_at,revoked,consumed FROM invitations ORDER BY created_at", r => new InvitationInfo(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4), r.GetBoolean(5)));
    public async Task RevokeInvitation(Guid id)
    {
        if (await Run("UPDATE invitations SET revoked=true WHERE id=$1 AND NOT consumed", id) == 0) throw new AuthError(404, "invitation_not_found");
    }
    public async Task<Account> Register(Registration input)
    {
        var hash = TokenHash(input.Token);
        var invitations = await Query("SELECT id,email FROM invitations WHERE token_hash=$1 AND expires_at>$2 AND NOT revoked AND NOT consumed", r => (Id: r.GetGuid(0), Email: r.GetString(1)), hash, Now);
        if (invitations.Count == 0) throw new AuthError(400, "invalid_token");
        var invitation = invitations[0];
        var account = new Account(Guid.NewGuid(), Username(input.Username), invitation.Email, false, true, HashPassword(input.Password));
        await InsertAccount(account);
        await Run("UPDATE invitations SET consumed=true, account_id=$2 WHERE id=$1", invitation.Id, account.Id);
        return account;
    }
    public async Task<object> CreateReset(Guid accountId, string origin)
    {
        var account = await AccountById(accountId);
        if (!account.IsActive) throw new AuthError(409, "account_disabled");
        var token = Secret();
        var expires = Now.AddHours(1);
        await Run("INSERT INTO password_resets VALUES ($1,$2,$3) ON CONFLICT (account_id) DO UPDATE SET token_hash=EXCLUDED.token_hash, expires_at=EXCLUDED.expires_at", accountId, Fingerprint(token), expires);
        return new { expiresAt = expires, link = $"{origin}/reset-password#token={token}" };
    }
    public async Task Reset(ResetPassword input)
    {
        var id = await Scalar("SELECT r.account_id FROM password_resets r JOIN accounts a ON a.id=r.account_id WHERE r.token_hash=$1 AND r.expires_at>$2 AND a.is_active", TokenHash(input.Token), Now);
        if (id is not Guid accountId) throw new AuthError(400, "invalid_token");
        await StorePassword(accountId, input.Password);
        await RevokeAll(accountId);
    }
}
