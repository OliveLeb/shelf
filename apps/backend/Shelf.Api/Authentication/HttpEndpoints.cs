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

        // Validate browser credentials before JSON binding, rate limiting or opening a transaction.
        app.Use(async (http, next) =>
        {
            if (http.GetEndpoint()?.Metadata.GetMetadata<SessionTransport.Policy>() is not null)
            {
                http.Response.Headers.CacheControl = "no-store";
                try { SessionTransport.Validate(http, origin); }
                catch (AuthError error) { await SessionTransport.Reject(http, error).ExecuteAsync(http); return; }
            }
            await next(http);
        });
        app.UseRateLimiter();
        var api = app.MapGroup("").WithMetadata(new SessionTransport.Policy());
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var authorization = SessionTransport.Authorization(http);
                var result = await http.RequestServices.GetRequiredService<Database>().Execute(async db =>
                {
                    http.Items[typeof(AuthTransaction)] = db;
                    if (http.GetEndpoint()?.Metadata.GetMetadata<Access>() is { } access)
                        http.Items[typeof(Actor)] = await db.Authenticate(authorization, access.Admin);
                    return await next(context);
                }, http.RequestServices.GetRequiredService<TimeProvider>(), http.RequestAborted);
                if (http.Items[typeof(LoginResult)] is LoginResult login)
                    SessionTransport.Issue(http, login.Token, login.ExpiresAt);
                else if (http.Items[typeof(Actor)] is Actor actor)
                    SessionTransport.Renew(http, actor, result);
                return result;
            }
            catch (AuthError error) { return SessionTransport.Reject(http, error); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { error = "identifier_conflict" }); }
            catch (NpgsqlException) { return Results.Json(new { error = "database_unavailable" }, statusCode: 503); }
        });

        api.MapPost("/auth/register", async (Registration input, HttpContext h) => Results.Json(await Db(h).Register(input), statusCode: 201)).Produces<Account>(201).WithTags("Authentication").AllowAnonymous().RequireRateLimiting("public-auth").WithSummary("Register with an invitation");
        api.MapPost("/auth/login", async (Login input, HttpContext h) => Results.Ok(await Db(h).Login(input))).Produces<LoginResult>().WithTags("Authentication").AllowAnonymous().RequireRateLimiting("public-auth").WithSummary("Log in")
            .WithMetadata(new SessionTransport.Policy(MobileLogin: true))
            .WithDescription("Mobile Bearer login. Returns the opaque token. With a web session cookie, rejects with 400 web_login_required after CSRF validation; use /auth/web/login instead.")
            .Produces(400).Produces(429);
        api.MapPost("/auth/web/login", async (Login input, HttpContext h) =>
        {
            var login = await Db(h).WebLogin(input, h.Request.Cookies[SessionTransport.CookieName]);
            h.Items[typeof(LoginResult)] = login;
            return Results.Ok(login.Account);
        }).Produces<Account>().WithTags("Authentication").AllowAnonymous().RequireRateLimiting("public-auth").WithSummary("Log in on the web")
            .WithMetadata(new SessionTransport.Policy(WebLogin: true))
            .WithDescription("Returns Account only, never the token or session envelope. Sets __Host-shelf-session (HttpOnly; Secure; SameSite=Strict; Path=/; no Domain), expiring with the 30-day sliding inactivity window. A successful login atomically revokes any session identified by the previous web cookie, including on account switch.")
            .Produces(400).Produces(429);
        api.MapPost("/auth/reset-password", async (ResetPassword input, HttpContext h) => { await Db(h).Reset(input); return Results.NoContent(); }).Produces(204).WithTags("Authentication").AllowAnonymous().RequireRateLimiting("public-auth").WithSummary("Reset password with a reset link");

        var user = api.MapGroup("").WithMetadata(new Access(false)).WithTags("Account");
        user.MapGet("/account", (HttpContext h) => Results.Ok(Current(h).Account)).Produces<Account>().WithSummary("Get current account");
        user.MapPatch("/account/username", async (UsernameChange input, HttpContext h) => Results.Ok(await Db(h).ChangeUsername(Current(h), input.Username))).Produces<Account>().WithSummary("Change username");
        user.MapPatch("/account/email", async (EmailChange input, HttpContext h) => Results.Ok(await Db(h).ChangeEmail(Current(h), input))).Produces<Account>().WithSummary("Change email");
        user.MapPatch("/account/password", async (PasswordChange input, HttpContext h) => { await Db(h).ChangePassword(Current(h), input); return Results.NoContent(); }).Produces(204).WithSummary("Change password");
        user.MapDelete("/account", async (HttpContext h, [Microsoft.AspNetCore.Mvc.FromBody] PasswordConfirmation input) => { await Db(h).Delete(Current(h).Account.Id, input.CurrentPassword, self: true); return Results.NoContent(); }).Produces(204).WithSummary("Delete own account");
        user.MapGet("/account/sessions", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Sessions(Current(h)))).Produces<List<SessionInfo>>().WithSummary("List active sessions");
        user.MapDelete("/account/sessions/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).RevokeSession(Current(h), id); return Results.NoContent(); }).Produces(204).WithSummary("Revoke a session");
        user.MapDelete("/account/sessions", async (HttpContext h, CancellationToken _) => { await Db(h).RevokeAll(Current(h).Account.Id); return Results.NoContent(); }).Produces(204).WithSummary("Revoke all sessions");
        user.MapPost("/auth/logout", async (HttpContext h, CancellationToken _) => { await Db(h).RevokeSession(Current(h), Current(h).SessionId); return Results.NoContent(); }).Produces(204).WithSummary("Log out")
            .WithMetadata(new SessionTransport.Policy(Logout: true))
            .WithDescription("Revokes only the selected session. A web logout deletes the host-only session cookie with the same Secure, HttpOnly, SameSite and Path attributes.");

        var admin = api.MapGroup("/admin").WithMetadata(new Access(true)).WithTags("Admin");
        admin.MapGet("/accounts", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Accounts())).Produces<List<Account>>().WithSummary("List accounts");
        admin.MapPost("/accounts/{id:guid}/disable", async (Guid id, HttpContext h) => { await Db(h).Disable(id); return Results.NoContent(); }).Produces(204).WithSummary("Disable an account");
        admin.MapDelete("/accounts/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).Delete(id); return Results.NoContent(); }).Produces(204).WithSummary("Delete an account");
        admin.MapPost("/accounts/{id:guid}/password-reset", async (Guid id, HttpContext h) => Results.Ok(await Db(h).CreateReset(id, origin))).Produces<ResetLink>().WithSummary("Create a password reset link");
        admin.MapPost("/invitations", async (InvitationRequest input, HttpContext h) => Results.Json(await Db(h).Invite(input.Email, origin), statusCode: 201)).Produces<InvitationLink>(201).WithSummary("Invite by email");
        admin.MapGet("/invitations", async (HttpContext h, CancellationToken _) => Results.Ok(await Db(h).Invitations())).Produces<List<InvitationInfo>>().WithSummary("List invitations");
        admin.MapDelete("/invitations/{id:guid}", async (Guid id, HttpContext h) => { await Db(h).RevokeInvitation(id); return Results.NoContent(); }).Produces(204).WithSummary("Revoke an invitation");
    }
}
