using Shelf.Api.Authentication;

var command = args.FirstOrDefault() is "migrate" or "admin";
var builder = WebApplication.CreateBuilder(command ? [] : args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddHealthChecks();
builder.AddAuthenticationBackend();
var app = builder.Build();
if (command)
{
    try
    {
        Environment.ExitCode = await OperatorCommands.Run(args, app.Services.GetRequiredService<Database>(), app.Services.GetRequiredService<TimeProvider>());
    }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine("invalid_configuration");
        Environment.ExitCode = 5;
    }
    finally { await app.DisposeAsync(); }
}
else
{
    app.MapHealthChecks("/health");
    app.MapAuthenticationBackend();
    app.Run();
}

public partial class Program;
