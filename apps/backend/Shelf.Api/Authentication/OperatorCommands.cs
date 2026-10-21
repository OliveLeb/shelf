using Npgsql;

namespace Shelf.Api.Authentication;

public static class OperatorCommands
{
    public static async Task<int> Run(string[] args, Database database, TimeProvider clock)
    {
        try
        {
            if (args is ["migrate"])
            {
                await database.Migrate(clock);
                Console.WriteLine("Database migrations applied.");
                return 0;
            }
            if (args is ["admin", "create", var username, var email])
            {
                var password = ReadPassword();
                var account = await database.Execute(db => db.CreateAdmin(username, email, password), clock);
                Console.WriteLine(account.Id);
                return 0;
            }
            if (args.Length == 3 && args[0] == "admin" && Guid.TryParse(args[2], out var id))
            {
                if (args[1] == "reset-password")
                {
                    var password = ReadPassword();
                    await database.Execute(async db => { await db.RecoverAdmin(id, password); return 0; }, clock);
                }
                else if (args[1] is "grant" or "revoke")
                    await database.Execute(async db => { await db.SetAdmin(id, args[1] == "grant"); return 0; }, clock);
                else return Usage();
                Console.WriteLine("Operation completed.");
                return 0;
            }
            return Usage();
        }
        catch (AuthError error) { Console.Error.WriteLine(error.Code); return error.Status == 404 ? 3 : error.Status == 409 ? 4 : 2; }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        { Console.Error.WriteLine("identifier_conflict"); return 4; }
        catch (NpgsqlException) { Console.Error.WriteLine("database_unavailable_or_migration_required"); return 5; }
    }
    private static int Usage()
    {
        Console.Error.WriteLine("Usage: migrate | admin create <username> <email> | admin grant|revoke|reset-password <account-id>");
        return 2;
    }
    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        Console.Error.Write("New password: ");
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
