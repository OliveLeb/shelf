using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Shelf.Api.Authentication;

public sealed partial class AuthTransaction
{
    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword("", "unused-timing-comparison-password");
    private const string AccountColumns = "id, username, email, is_admin, is_active, password_hash";
    private static Account ReadAccount(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5));
    public async Task<Account> AccountById(Guid id) => (await Query($"SELECT {AccountColumns} FROM accounts WHERE id=$1", ReadAccount, id)).SingleOrDefault()
        ?? throw new AuthError(404, "account_not_found");
    public Task<List<Account>> Accounts() => Query($"SELECT {AccountColumns} FROM accounts ORDER BY username", ReadAccount);
    private async Task<Account?> FindLogin(string identifier) => (await Query($"SELECT {AccountColumns} FROM accounts WHERE upper({(identifier.Contains('@') ? "email" : "username")})=upper($1)", ReadAccount, identifier)).SingleOrDefault();
    private static string Username(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Contains('@') || value.Any(char.IsControl)) throw new AuthError(400, "invalid_username");
        return value;
    }
    private static string Email(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 320 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || value.Any(char.IsControl)) throw new AuthError(400, "invalid_email");
        return value;
    }
    private static string HashPassword(string? value)
    {
        if (value is null || value.Length < 12 || value.Length > 1024) throw new AuthError(400, "invalid_password");
        return Hasher.HashPassword("", value);
    }
    private static bool Verify(Account? account, string? password) => password is { Length: <= 1024 } &&
        Hasher.VerifyHashedPassword("", account?.PasswordHash ?? DummyHash, password) != PasswordVerificationResult.Failed;
    private static void RequirePassword(Account account, string? password)
    {
        if (!Verify(account, password)) throw new AuthError(400, "incorrect_password");
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Fingerprint(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string TokenHash(string? token)
    {
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new AuthError(400, "invalid_token");
        return Fingerprint(token);
    }
    public async Task<Account> CreateAdmin(string? username, string? email, string? password)
    {
        var account = new Account(Guid.NewGuid(), Username(username), Email(email), true, true, HashPassword(password));
        await InsertAccount(account);
        return account;
    }
    private async Task InsertAccount(Account account)
    {
        await Run("INSERT INTO accounts VALUES ($1,$2,$3,$4,$5,$6)", account.Id, account.Username, account.Email, account.PasswordHash, account.IsAdmin, account.IsActive);
        // Every account owns exactly one personal space (storage module).
        await Run("INSERT INTO spaces (id, owner_id) VALUES ($1,$2)", Guid.NewGuid(), account.Id);
    }
    public async Task SetAdmin(Guid id, bool admin)
    {
        var account = await AccountById(id);
        if (!admin) await ProtectLastAdmin(account);
        await Run("UPDATE accounts SET is_admin=$2 WHERE id=$1", id, admin);
    }
    private async Task ProtectLastAdmin(Account account)
    {
        if (account.IsAdmin && account.IsActive && (long)(await Scalar("SELECT count(*) FROM accounts WHERE is_admin AND is_active"))! <= 1)
            throw new AuthError(409, "last_active_administrator");
    }
    public async Task Disable(Guid id)
    {
        var account = await AccountById(id);
        await ProtectLastAdmin(account);
        await Run("UPDATE accounts SET is_active=false WHERE id=$1", id);
        await RevokeAll(id);
        await Run("DELETE FROM password_resets WHERE account_id=$1", id);
    }
    public async Task Delete(Guid id, string? password = null, bool self = false)
    {
        var account = await AccountById(id);
        if (self) RequirePassword(account, password);
        await ProtectLastAdmin(account);
        await Run("DELETE FROM invitations WHERE upper(email)=upper($1)", account.Email);
        await Run("DELETE FROM accounts WHERE id=$1", id);
    }
    public async Task<Account> ChangeUsername(Actor actor, string? username)
    {
        await Run("UPDATE accounts SET username=$2 WHERE id=$1", actor.Account.Id, Username(username));
        return await AccountById(actor.Account.Id);
    }
    public async Task<Account> ChangeEmail(Actor actor, EmailChange input)
    {
        RequirePassword(actor.Account, input.CurrentPassword);
        await Run("UPDATE accounts SET email=$2 WHERE id=$1", actor.Account.Id, Email(input.Email));
        await RevokeOthers(actor);
        return await AccountById(actor.Account.Id);
    }
    public async Task ChangePassword(Actor actor, PasswordChange input)
    {
        RequirePassword(actor.Account, input.CurrentPassword);
        await StorePassword(actor.Account.Id, input.Password);
        await RevokeOthers(actor);
    }
    private async Task StorePassword(Guid id, string? password)
    {
        await Run("UPDATE accounts SET password_hash=$2 WHERE id=$1", id, HashPassword(password));
        await Run("DELETE FROM password_resets WHERE account_id=$1", id);
    }
    public async Task RecoverAdmin(Guid id, string? password)
    {
        if (!(await AccountById(id)).IsAdmin) throw new AuthError(400, "administrator_required");
        await StorePassword(id, password);
        await RevokeAll(id);
    }
}
