using Npgsql;
using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public static class HttpEndpoints
{
    private static StorageTransaction Db(HttpContext context) => (StorageTransaction)context.Items[typeof(StorageTransaction)]!;
    private static Account Current(HttpContext context) => (Account)context.Items[typeof(Account)]!;

    public static void AddStorageBackend(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(services => new ObjectStore(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddHostedService<UploadCleanup>();
    }

    public static void MapStorageBackend(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<ObjectStore>();
        var api = app.MapGroup("").WithTags("Storage");
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var clock = http.RequestServices.GetRequiredService<TimeProvider>();
            try
            {
                await using var connection = await http.RequestServices.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(http.RequestAborted);
                await using var transaction = await connection.BeginTransactionAsync(http.RequestAborted);
                // Storage authenticates in its own transaction, without the account operations' advisory lock.
                var actor = await new AuthTransaction(connection, transaction, clock, http.RequestAborted).Authenticate(http.Request.Headers.Authorization, admin: false);
                http.Items[typeof(Account)] = actor.Account;
                http.Items[typeof(StorageTransaction)] = new StorageTransaction(connection, transaction, store, clock, http.RequestAborted);
                var result = await next(context);
                await transaction.CommitAsync(http.RequestAborted);
                return result;
            }
            catch (AuthError error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (StorageError error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { error = "name_conflict" }); }
            catch (NpgsqlException) { return Results.Json(new { error = "database_unavailable" }, statusCode: 503); }
            catch (Amazon.Runtime.AmazonServiceException) { return Results.Json(new { error = "storage_unavailable" }, statusCode: 503); }
        });

        api.MapGet("/spaces/personal/elements", async (string? sort, string? order, HttpContext h) => Results.Ok(await Db(h).Content(Current(h), null, sort, order))).Produces<List<Element>>().WithSummary("List the personal space root");
        api.MapPost("/uploads", async (UploadRequest input, HttpContext h) => Results.Json(await Db(h).Declare(Current(h), input), statusCode: 201)).Produces<UploadTicket>(201).WithSummary("Declare an upload");
        api.MapGet("/uploads/{id:guid}", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Resume(Current(h), id))).Produces<UploadTicket>().WithSummary("Resume an upload");
        api.MapPost("/uploads/{id:guid}/complete", async (Guid id, HttpContext h) => Results.Json(await Db(h).Complete(Current(h), id), statusCode: 201)).Produces<Element>(201).WithSummary("Complete an upload");
        api.MapGet("/files/{id:guid}/download", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Download(Current(h), id))).Produces<SignedUrl>().WithSummary("Get a download link");
        api.MapGet("/folders/{id:guid}/elements", async (Guid id, string? sort, string? order, HttpContext h) => Results.Ok(await Db(h).Content(Current(h), id, sort, order))).Produces<List<Element>>().WithSummary("List a folder");
        api.MapPost("/folders", async (FolderRequest input, HttpContext h) => Results.Json(await Db(h).CreateFolder(Current(h), input), statusCode: 201)).Produces<Element>(201).WithSummary("Create a folder");
        api.MapPost("/elements/{id:guid}/rename", async (Guid id, RenameRequest input, HttpContext h) => Results.Ok(await Db(h).Rename(Current(h), id, input))).Produces<Element>().WithSummary("Rename an element");
        api.MapPost("/elements/{id:guid}/move", async (Guid id, MoveRequest input, HttpContext h) => Results.Ok(await Db(h).Move(Current(h), id, input))).Produces<Element>().WithSummary("Move an element");
    }
}
