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
                var actor = await new AuthTransaction(connection, transaction, clock, http.RequestAborted).Authenticate(http.Request.Headers.Authorization, admin: false, accountMutation: false);
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

        api.MapGet("/shares/received", (Delegate)(async (HttpContext h) => Results.Ok(await Db(h).ReceivedShares(Current(h))))).Produces<List<SharedElement>>().WithSummary("List elements shared with me");
        api.MapGet("/elements/{id:guid}/shares", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Shares(Current(h), id))).Produces<List<Share>>().WithSummary("List an element's shares");
        api.MapPut("/elements/{id:guid}/shares/{recipientId:guid}", async (Guid id, Guid recipientId, ShareRequest input, HttpContext h) => Results.Ok(await Db(h).SetShare(Current(h), id, recipientId, input))).Produces<Share>().WithSummary("Set a share's level")
            .WithDescription("Only the owner may grant read or write. Write permits editing contents, not renaming, moving or deleting a shared root. Overlapping grants use the highest level.");
        api.MapDelete("/elements/{id:guid}/shares/{recipientId:guid}", async (Guid id, Guid recipientId, HttpContext h) => { await Db(h).RemoveShare(Current(h), id, recipientId); return Results.NoContent(); }).Produces(204).WithSummary("Remove a share");
        api.MapGet("/spaces/personal/elements", async (string? sort, string? order, HttpContext h) => Results.Ok(await Db(h).Content(Current(h), null, sort, order))).Produces<List<Element>>().WithSummary("List the personal space root");
        api.MapGet("/spaces/common/elements", async (string? sort, string? order, HttpContext h) => Results.Ok(await Db(h).Content(Current(h), null, sort, order, "common"))).Produces<List<Element>>().WithSummary("List the common space root");
        api.MapGet("/uploads/{id:guid}", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Resume(Current(h), id))).Produces<UploadTicket>().WithSummary("Resume an upload");
        api.MapPost("/uploads", async (UploadRequest input, HttpContext h) => Results.Json(await Db(h).Declare(Current(h), input), statusCode: 201)).Produces<UploadTicket>(201).WithSummary("Declare an upload")
            .WithDescription("Requires write access to the destination. To replace a specific writable file, including a shared file with a hidden parent, send replaceFileId, size and contentType without name, parentId, space or onConflict. Old content goes to the owner's trash at confirmation.");
        api.MapPost("/uploads/{id:guid}/complete", async (Guid id, HttpContext h) => Results.Json(await Db(h).Complete(Current(h), id), statusCode: 201)).Produces<Element>(201).WithSummary("Complete an upload")
            .WithDescription("Rechecks write access and uploaded size before publishing. A targeted replacement preserves file identity and shares; revoked, read-only or trashed targets cannot be replaced.");
        api.MapGet("/files/{id:guid}/download", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Download(Current(h), id))).Produces<SignedUrl>().WithSummary("Get a download link");
        api.MapGet("/folders/{id:guid}/elements", async (Guid id, string? sort, string? order, HttpContext h) => Results.Ok(await Db(h).Content(Current(h), id, sort, order))).Produces<List<Element>>().WithSummary("List a folder");
        api.MapPost("/folders", async (FolderRequest input, HttpContext h) => Results.Json(await Db(h).CreateFolder(Current(h), input), statusCode: 201)).Produces<Element>(201).WithSummary("Create a folder");
        api.MapPost("/elements/{id:guid}/rename", async (Guid id, RenameRequest input, HttpContext h) => Results.Ok(await Db(h).Rename(Current(h), id, input))).Produces<Element>().WithSummary("Rename an element");
        api.MapPost("/elements/{id:guid}/move", async (Guid id, MoveRequest input, HttpContext h) => Results.Ok(await Db(h).Move(Current(h), id, input))).Produces<Element>().WithSummary("Move an element");
        api.MapPost("/elements/{id:guid}/copy", async (Guid id, CopyRequest input, HttpContext h) => Results.Json(await Db(h).Copy(Current(h), id, input), statusCode: 201)).Produces<Element>(201).WithSummary("Copy an element");
        api.MapDelete("/elements/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).Delete(Current(h), id); return Results.NoContent(); }).Produces(204).WithSummary("Move an element to its space trash");
        api.MapGet("/spaces/{space}/trash", async (string space, HttpContext h) => Results.Ok(await Db(h).Trash(Current(h), space))).Produces<List<TrashedElement>>().WithSummary("List a space trash");
        api.MapPost("/trash/{id:guid}/restore", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Restore(Current(h), id))).Produces<Element>().WithSummary("Restore a trashed element");
        api.MapDelete("/trash/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).Purge(Current(h), id); return Results.NoContent(); }).Produces(204).WithSummary("Permanently purge a trashed element");
        api.MapDelete("/spaces/{space}/trash", async (string space, HttpContext h) => { await Db(h).EmptyTrash(Current(h), space); return Results.NoContent(); }).Produces(204).WithSummary("Empty a space trash");
    }
}
