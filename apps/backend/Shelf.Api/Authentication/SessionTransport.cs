namespace Shelf.Api.Authentication;

public static class SessionTransport
{
    public const string CookieName = "__Host-shelf-session";
    public sealed record Policy(bool WebLogin = false, bool MobileLogin = false, bool Logout = false);

    public static void Validate(HttpContext http, string clientOrigin)
    {
        _ = Authorization(http);
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<Policy>();
        var unsafeMethod = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method);
        if (policy?.WebLogin == true || (HasCookie(http) && unsafeMethod))
        {
            var origin = http.Request.Headers.Origin;
            var csrf = http.Request.Headers["X-Shelf-CSRF"];
            if (origin.Count != 1 || origin[0] != clientOrigin ||
                csrf.Count != 1 || csrf[0] != "1")
                throw new AuthError(403, "csrf_rejected");
        }
        if (policy?.MobileLogin == true && HasCookie(http)) throw new AuthError(400, "web_login_required");
    }

    private static CookieOptions Options(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires
    };

    public static void Issue(HttpContext http, string token, DateTimeOffset expires) =>
        http.Response.Cookies.Append(CookieName, token, Options(expires));

    // ASP.NET's parsed collection drops empty cookies. Their presence must still prevent Bearer fallback.
    public static bool HasCookie(HttpContext http) => http.Request.Headers.Cookie.Any(header =>
        header?.Split(';').Any(part => part.Split('=', 2)[0].Trim() == CookieName) == true);

    public static string? Authorization(HttpContext http)
    {
        if (!HasCookie(http)) return http.Request.Headers.Authorization;
        if (http.Request.Headers.ContainsKey("Authorization")) throw new AuthError(401, "ambiguous_credentials");
        return "Bearer " + http.Request.Cookies[CookieName];
    }

    public static void Clear(HttpContext http) => http.Response.Cookies.Delete(CookieName, Options());

    public static void Renew(HttpContext http, Actor actor, object? result)
    {
        if (!HasCookie(http) || result is IStatusCodeHttpResult { StatusCode: >= 400 }) return;
        if (http.GetEndpoint()?.Metadata.GetMetadata<Policy>()?.Logout == true) Clear(http);
        else Issue(http, http.Request.Cookies[CookieName]!, actor.ExpiresAt);
    }

    public static IResult Reject(HttpContext http, AuthError error)
    {
        if (error.Status == 401 && HasCookie(http)) Clear(http);
        return Results.Json(new { error = error.Code }, statusCode: error.Status);
    }
}
