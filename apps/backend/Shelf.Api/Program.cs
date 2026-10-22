using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Shelf.Api.Authentication;
using Shelf.Api.Storage;

var command = args.FirstOrDefault() is "migrate" or "admin";
var builder = WebApplication.CreateBuilder(command ? [] : args);
// Build-time OpenAPI generation (contracts/openapi.json) starts the app without deployment configuration.
var generatingContract = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
if (generatingContract)
{
    builder.Configuration["Authentication:ClientOrigin"] ??= "http://localhost";
    builder.Configuration["Storage:S3:Endpoint"] ??= "http://localhost";
    foreach (var key in new[] { "Bucket", "AccessKey", "SecretKey", "Region" })
        builder.Configuration[$"Storage:S3:{key}"] ??= "openapi";
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    // Routes require a session token unless they are marked AllowAnonymous().
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Shelf API";
        document.Servers = [new OpenApiServer { Url = "http://localhost:5050", Description = "Local" }];
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            (operation.Security ??= []).Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] });
        return Task.CompletedTask;
    });
});
builder.AddAuthenticationBackend();
builder.AddStorageBackend();
if (!command && !generatingContract) builder.Services.AddHostedService<TrashWorker>();
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
    if (app.Environment.IsDevelopment()) app.MapOpenApi();
    app.MapHealthChecks("/health");
    app.MapAuthenticationBackend();
    app.MapStorageBackend();
    app.Run();
}

public partial class Program;
