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
        var api = app.MapGroup("").WithTags("Storage").WithMetadata(new SessionTransport.Policy());
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var clock = http.RequestServices.GetRequiredService<TimeProvider>();
            try
            {
                var authorization = SessionTransport.Authorization(http);
                await using var connection = await http.RequestServices.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(http.RequestAborted);
                // Commit activity before the long operation, without the account advisory lock.
                await using (var activity = await connection.BeginTransactionAsync(http.RequestAborted))
                {
                    await new AuthTransaction(connection, activity, clock, http.RequestAborted).Authenticate(authorization, admin: false, accountMutation: false);
                    await activity.CommitAsync(http.RequestAborted);
                }
                await using var transaction = await connection.BeginTransactionAsync(http.RequestAborted);
                // Revalidate access and protect it through commit; activity must not hold an exclusive lock here.
                var actor = await new AuthTransaction(connection, transaction, clock, http.RequestAborted).Authenticate(authorization, admin: false, accountMutation: false, updateActivity: false);
                http.Items[typeof(Account)] = actor.Account;
                http.Items[typeof(StorageTransaction)] = new StorageTransaction(connection, transaction, store, clock, http.RequestAborted);
                var result = await next(context);
                await transaction.CommitAsync(http.RequestAborted);
                SessionTransport.Renew(http, actor, result);
                return result;
            }
            catch (AuthError error) { return SessionTransport.Reject(http, error); }
            catch (StorageError error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { error = "name_conflict" }); }
            catch (NpgsqlException) { return Results.Json(new { error = "database_unavailable" }, statusCode: 503); }
            catch (Amazon.Runtime.AmazonServiceException) { return Results.Json(new { error = "storage_unavailable" }, statusCode: 503); }
        });

        api.MapGet("/elements/search", async (string? query, HttpContext h) => TypedResults.Ok(await Db(h).Search(Current(h), query)))
            .WithSummary("Search visible elements by name")
            .WithDescription("Case-insensitive literal substring (1–255 characters, not whitespace-only). Includes personal, common and received shared elements, without trash or duplicates.")
            .Produces(400);
        api.MapGet("/storage/usage", (Delegate)(async (HttpContext h) => TypedResults.Ok(await Db(h).Usage(Current(h)))))
            .WithSummary("Get personal and common storage usage")
            .WithDescription("Confirmed file bytes including trash, excluding pending uploads and thumbnails. No quota is applied.");
        api.MapGet("/admin/storage/usage", (Delegate)(async (HttpContext h) => TypedResults.Ok(await Db(h).AccountUsage(Current(h)))))
            .WithSummary("Get storage usage per account")
            .WithDescription("Administrator only. Returns account identity and personal-space file bytes including trash, without content metadata. Includes empty and disabled accounts; excludes common-space bytes.")
            .Produces(403);
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
            .WithDescription("Rechecks write access and uploaded size, then conditionally copies the validated object to a private final key. Replaying an upload URL cannot modify confirmed content. A concurrent content change returns 409 source_changed; resend and retry confirmation. A targeted replacement preserves file identity and shares; revoked, read-only or trashed targets cannot be replaced.")
            .Produces(409);
        api.MapGet("/files/{id:guid}/download", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Download(Current(h), id))).Produces<SignedUrl>().WithSummary("Get a download link");
        api.MapGet("/files/{id:guid}/thumbnail", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Thumbnail(Current(h), id))).Produces<ThumbnailResult>().WithSummary("Get a miniature state and link")
            .WithDescription("Uses the file's read access. Link is null while pending or unavailable; generation failure never prevents downloading the file.");
        api.MapGet("/files/{id:guid}/stream", async (Guid id, HttpContext h) => Results.Ok(await Db(h).StreamVideo(Current(h), id))).Produces<SignedUrl>().WithSummary("Get a video streaming link")
            .WithDescription("Inline signed GET of the original video, with native S3 Range support and no transcoding. Request a fresh link after expiry; unsupported codecs remain downloadable.");
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
