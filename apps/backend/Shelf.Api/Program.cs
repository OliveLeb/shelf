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
    builder.Configuration["ConnectionStrings:Shelf"] ??= "Host=localhost";
    builder.Configuration["Storage:S3:Endpoint"] ??= "http://localhost";
    foreach (var key in new[] { "Bucket", "AccessKey", "SecretKey", "Region" })
        builder.Configuration[$"Storage:S3:{key}"] ??= "openapi";
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    // Protected routes accept exactly one opaque-session transport: Bearer OR web cookie.
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Shelf API";
        document.Servers = [new OpenApiServer { Url = "http://localhost:5050", Description = "Local" }];
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" };
        document.Components.SecuritySchemes["WebSession"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = SessionTransport.CookieName,
            Description = "Persistent HttpOnly Secure SameSite=Strict host-only cookie, Path=/. Never combine with Authorization."
        };
        document.Components.SecuritySchemes["WebCsrf"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Shelf-CSRF",
            Description = "Value must be exactly 1. Web unsafe requests also require Origin exactly equal to Authentication:ClientOrigin; no permissive CORS is provided."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var policy = metadata.OfType<SessionTransport.Policy>().LastOrDefault();
        var protectedRoute = !metadata.OfType<IAllowAnonymous>().Any();
        var unsafeMethod = context.Description.HttpMethod is not ("GET" or "HEAD" or "OPTIONS");
        if (protectedRoute)
        {
            (operation.Security ??= []).Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] });
            var cookie = new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("WebSession", context.Document)] = [] };
            if (unsafeMethod) cookie[new OpenApiSecuritySchemeReference("WebCsrf", context.Document)] = [];
            operation.Security.Add(cookie);
        }
        if (policy is not null)
        {
            var anonymousWeb = policy.WebLogin || policy.WebRegistration;
            var webOperation = policy.WebRegistration ? "web registration" : "web login";
            operation.Description = (operation.Description is null ? "" : operation.Description + "\n\n") +
                "Cookie and Authorization together are rejected (401); no credential fallback. Rejected cookies are cleared on 401. Cookies renew only after successful authenticated activity.";
            if (unsafeMethod)
            {
                operation.Description += $" Web requests (cookie present, and always {webOperation}) require exact configured Origin and X-Shelf-CSRF: 1, otherwise 403. No permissive API CORS.";
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Origin", In = ParameterLocation.Header, Required = anonymousWeb,
                    Description = $"Exactly Authentication:ClientOrigin. Required with a web cookie and always for {webOperation}. Missing, null or foreign origins are rejected.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-Shelf-CSRF", In = ParameterLocation.Header, Required = anonymousWeb,
                    Description = anonymousWeb
                        ? $"Exactly 1. Always required for {webOperation}, even without a cookie or with Bearer credentials."
                        : $"Exactly 1. Required with a web cookie and always for {webOperation}; not required for Bearer-only mobile requests.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^1$" }
                });
                if (anonymousWeb)
                    (operation.Security ??= []).Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("WebCsrf", context.Document)] = [] });
            }
            operation.Responses ??= new();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Invalid/expired/revoked session or credentials, or mixed cookie and Authorization. Rejected web cookies are deleted." });
            if (unsafeMethod || protectedRoute)
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Insufficient access, or missing/invalid Origin or X-Shelf-CSRF on a web unsafe request." });
        }
        return Task.CompletedTask;
    });
});
builder.AddAuthenticationBackend();
builder.AddStorageBackend();
if (!command && !generatingContract)
{
    builder.Services.AddHostedService<TrashWorker>();
    builder.Services.AddHostedService<ThumbnailWorker>();
}
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
