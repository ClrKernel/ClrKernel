using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ClrKernel.Studio;

/// <summary>
/// Resolves the session cookie onto the request, and turns away anyone who has not
/// signed in.
/// <para>
/// Two different refusals on purpose. An <c>/api</c> call gets 401 — a redirect to
/// an HTML page is a useless answer to fetch(), and one that a client will happily
/// parse as data. A browser asking for a page gets a redirect, because landing on
/// a blank screen and being told to guess the URL is not an answer either.
/// </para>
/// </summary>
public sealed class AuthenticationMiddleware {
    /// <summary>Pages that exist precisely for people who are not signed in.</summary>
    private static readonly string[] _anonymousPages = { "/signin", "/setup", "/invite" };

    private readonly RequestDelegate _next;

    public AuthenticationMiddleware(RequestDelegate next) {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthService auth) {
        var token = context.Request.Cookies[AuthService.CookieName];
        var (session, user) = await auth.ResolveSessionAsync(token);
        if (user != null) {
            context.Items[AuthContext.UserItem] = user;
            // Last-seen is a rough clock, not an audit trail: once an hour is
            // plenty and a write on every request is not.
            if (session.LastSeenAt < System.DateTime.UtcNow.AddHours(-1)) {
                await auth.TouchSessionAsync(token);
            }
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        if (context.Request.Path.StartsWithSegments("/api")) {
            if (AuthApi.IsPublic(path)) {
                await _next(context);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Sign in to use this server." });
            return;
        }

        // Anything that is not a document request — the SPA bundle, fonts, the
        // favicon — is served as usual. Redirecting those would break the very
        // page being redirected to.
        if (!IsDocumentRequest(context) || IsAnonymousPage(path)) {
            await _next(context);
            return;
        }

        // An empty user table means the server has not been claimed yet, and every
        // door leads to the same place.
        var target = await auth.UserCountAsync() == 0 ? "/setup" : "/signin";
        context.Response.Redirect(target);
    }

    private static bool IsAnonymousPage(PathString path) =>
        _anonymousPages.Any(page => path.StartsWithSegments(page));

    /// <summary>
    /// A browser navigating, rather than a script fetching. `Accept: text/html` is
    /// what distinguishes the two, and it is what the SPA's own asset requests do
    /// not send.
    /// </summary>
    private static bool IsDocumentRequest(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers.Accept.ToString().Contains("text/html");
}
