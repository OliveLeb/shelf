using System.Reflection;
using Npgsql;

namespace Shelf.Api.Authentication;

public sealed class Database(NpgsqlDataSource source)
{
    // All account operations share this transaction lock, including CLI processes and API replicas.
    // Keep the lock until commit so authorization and the last-admin check cannot race a mutation.
    private const long LockId = 742013510;

    public async Task<T> Execute<T>(Func<AuthTransaction, Task<T>> action, TimeProvider clock, CancellationToken cancellation = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            command.Parameters.AddWithValue(LockId);
            await command.ExecuteNonQueryAsync(cancellation);
        }
        var result = await action(new AuthTransaction(connection, transaction, clock, cancellation));
        await transaction.CommitAsync(cancellation);
        return result;
    }

    public Task<int> Migrate(TimeProvider clock) => Execute(async db =>
    {
        await db.Run("CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY)");
        const string prefix = "Shelf.Api.Migrations.";
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = int.Parse(resource[prefix.Length..].Split('_')[0]);
            if (await db.Scalar("SELECT count(*) FROM schema_migrations WHERE version=$1", version) is long count && count > 0) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            await db.Run(await reader.ReadToEndAsync());
            await db.Run("INSERT INTO schema_migrations VALUES ($1)", version);
        }
        return 0;
    }, clock);
}

public sealed partial class AuthTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, TimeProvider clock, CancellationToken cancellation)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    private NpgsqlCommand Command(string sql, object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }

    public async Task<int> Run(string sql, params object[] values)
    {
        await using var command = Command(sql, values);
        return await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<object?> Scalar(string sql, params object[] values)
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
