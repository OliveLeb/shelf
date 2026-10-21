using Npgsql;
using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public static class HttpEndpoints
{
    private static StorageTransaction Db(HttpContext context) => (StorageTransaction)context.Items[typeof(StorageTransaction)]!;
    private static Account Current(HttpContext context) => (Account)context.Items[typeof(Account)]!;

    public static void AddStorageBackend(this WebApplicationBuilder builder) =>
        builder.Services.AddSingleton(services => new ObjectStore(services.GetRequiredService<IConfiguration>()));

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
                // Authentication belongs to the account module; storage work runs in its own transaction, without the account lock.
                var actor = await http.RequestServices.GetRequiredService<Database>().Execute(db => db.Authenticate(http.Request.Headers.Authorization, admin: false), clock, http.RequestAborted);
                await using var connection = await http.RequestServices.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(http.RequestAborted);
                await using var transaction = await connection.BeginTransactionAsync(http.RequestAborted);
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

        api.MapGet("/spaces/personal/elements", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).PersonalRoot(Current(h)))).Produces<List<FileElement>>().WithSummary("List personal space files");
        api.MapPost("/uploads", async (UploadRequest input, HttpContext h) => Results.Json(await Db(h).Declare(Current(h), input), statusCode: 201)).Produces<UploadTicket>(201).WithSummary("Declare an upload");
        api.MapPost("/uploads/{id:guid}/complete", async (Guid id, HttpContext h) => Results.Json(await Db(h).Complete(Current(h), id), statusCode: 201)).Produces<FileElement>(201).WithSummary("Complete an upload");
        api.MapGet("/files/{id:guid}/download", async (Guid id, HttpContext h) => Results.Ok(await Db(h).Download(Current(h), id))).Produces<SignedUrl>().WithSummary("Get a download link");
    }
}
