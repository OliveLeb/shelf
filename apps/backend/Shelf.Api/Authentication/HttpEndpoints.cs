using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using System.Threading.RateLimiting;

namespace Shelf.Api.Authentication;

public static class HttpEndpoints
{
    private sealed record Access(bool Admin);
    private static AuthTransaction Db(HttpContext context) => (AuthTransaction)context.Items[typeof(AuthTransaction)]!;
    private static Actor Current(HttpContext context) => (Actor)context.Items[typeof(Actor)]!;

    public static void AddAuthenticationBackend(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Shelf")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Shelf.")));
        builder.Services.AddSingleton<Database>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("public-auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = builder.Configuration.GetValue("Authentication:PublicPermitLimit", 10),
                    Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("Authentication:PublicWindowSeconds", 60)),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = async (context, cancellation) =>
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limit_exceeded" }, cancellation);
        });
    }

    public static void MapAuthenticationBackend(this WebApplication app)
    {
        var origin = app.Configuration["Authentication:ClientOrigin"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
            throw new InvalidOperationException("Set Authentication__ClientOrigin to an absolute HTTP(S) origin.");
        origin = uri.GetLeftPart(UriPartial.Authority);

        app.UseRateLimiter();
        var api = app.MapGroup("");
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                return await http.RequestServices.GetRequiredService<Database>().Execute(async db =>
                {
                    http.Items[typeof(AuthTransaction)] = db;
                    if (http.GetEndpoint()?.Metadata.GetMetadata<Access>() is { } access)
                        http.Items[typeof(Actor)] = await db.Authenticate(http.Request.Headers.Authorization, access.Admin);
                    return await next(context);
                }, http.RequestServices.GetRequiredService<TimeProvider>(), http.RequestAborted);
            }
            catch (AuthError error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { error = "identifier_conflict" }); }
            catch (NpgsqlException) { return Results.Json(new { error = "database_unavailable" }, statusCode: 503); }
        });

        api.MapPost("/auth/register", async (Registration input, HttpContext h) => Results.Json(await Db(h).Register(input), statusCode: 201)).RequireRateLimiting("public-auth");
        api.MapPost("/auth/login", async (Login input, HttpContext h) => Results.Ok(await Db(h).Login(input))).RequireRateLimiting("public-auth");
        api.MapPost("/auth/reset-password", async (ResetPassword input, HttpContext h) => { await Db(h).Reset(input); return Results.NoContent(); }).RequireRateLimiting("public-auth");

        var user = api.MapGroup("").WithMetadata(new Access(false));
        user.MapGet("/account", (HttpContext h) => Results.Ok(Current(h).Account));
        user.MapPatch("/account/username", async (UsernameChange input, HttpContext h) => Results.Ok(await Db(h).ChangeUsername(Current(h), input.Username)));
        user.MapPatch("/account/email", async (EmailChange input, HttpContext h) => Results.Ok(await Db(h).ChangeEmail(Current(h), input)));
        user.MapPatch("/account/password", async (PasswordChange input, HttpContext h) => { await Db(h).ChangePassword(Current(h), input); return Results.NoContent(); });
        user.MapDelete("/account", async (HttpContext h, [Microsoft.AspNetCore.Mvc.FromBody] PasswordConfirmation input) => { await Db(h).Delete(Current(h).Account.Id, input.CurrentPassword, self: true); return Results.NoContent(); });
        user.MapGet("/account/sessions", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Sessions(Current(h))));
        user.MapDelete("/account/sessions/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).RevokeSession(Current(h), id); return Results.NoContent(); });
        user.MapDelete("/account/sessions", async (HttpContext h, CancellationToken _) => { await Db(h).RevokeAll(Current(h).Account.Id); return Results.NoContent(); });
        user.MapPost("/auth/logout", async (HttpContext h, CancellationToken _) => { await Db(h).RevokeSession(Current(h), Current(h).SessionId); return Results.NoContent(); });

        var admin = api.MapGroup("/admin").WithMetadata(new Access(true));
        admin.MapGet("/accounts", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Accounts()));
        admin.MapPost("/accounts/{id:guid}/disable", async (Guid id, HttpContext h) => { await Db(h).Disable(id); return Results.NoContent(); });
        admin.MapDelete("/accounts/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).Delete(id); return Results.NoContent(); });
        admin.MapPost("/accounts/{id:guid}/password-reset", async (Guid id, HttpContext h) => Results.Ok(await Db(h).CreateReset(id, origin)));
        admin.MapPost("/invitations", async (InvitationRequest input, HttpContext h) => Results.Json(await Db(h).Invite(input.Email, origin), statusCode: 201));
        admin.MapGet("/invitations", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Invitations()));
        admin.MapDelete("/invitations/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).RevokeInvitation(id); return Results.NoContent(); });
    }
}
