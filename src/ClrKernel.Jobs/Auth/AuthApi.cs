using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Fido2NetLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClrKernel.Jobs;

/// <summary>Who is making this request, once the middleware has resolved the cookie.</summary>
public static class AuthContext {
    internal const string UserItem = "clrkernel.user";

    public static User CurrentUser(this HttpContext context) =>
        context.Items.TryGetValue(UserItem, out var user) ? user as User : null;

    public static bool IsAdmin(this HttpContext context) =>
        context.CurrentUser() is { Role: UserRole.ServerAdmin };

    /// <summary>
    /// The one gate for anything that writes or executes. Returns null when the
    /// caller may proceed, and the refusal otherwise.
    /// <para>
    /// A viewer being unable to run a cell is not a UI convenience: running a cell
    /// is arbitrary code execution on this machine. Hidden buttons are a courtesy;
    /// this is the boundary.
    /// </para>
    /// </summary>
    public static IResult RequireAdmin(this HttpContext context) {
        var user = context.CurrentUser();
        if (user == null) {
            return Results.Json(new { error = "Sign in first." }, statusCode: 401);
        }
        return user.Role == UserRole.ServerAdmin
            ? null
            : Results.Json(new { error = "Server Viewers cannot change or run anything." },
                statusCode: 403);
    }
}

/// <summary>
/// Marks a route as writing or executing. Applied at the route table rather than
/// inside each handler so the whole policy can be read in one pass — the question
/// "what can a viewer do" is answered by looking for the routes without it.
/// </summary>
public static class AdminOnlyExtensions {
    public static RouteHandlerBuilder AdminOnly(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequireAdmin() ?? await next(context));
}

/// <summary>
/// Sign-in, registration and account management. Everything here is deliberately
/// outside the role gate — these are the routes you use when you are nobody yet.
/// </summary>
public static class AuthApi {
    /// <summary>Paths under /api that an unauthenticated caller may reach.</summary>
    internal static bool IsPublic(PathString path) =>
        path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/health");

    public static void MapAuthApi(this IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api/auth");

        // Who am I, and what does this server want from me? The SPA asks this
        // first and routes on the answer.
        api.MapGet("/session", async (HttpContext context, AuthService auth, JobsOptions options) => {
            var user = context.CurrentUser();
            return Results.Ok(new {
                authenticated = user != null,
                needsSetup = await auth.UserCountAsync() == 0,
                // The browser refuses WebAuthn outside a secure context, and
                // saying so beats letting the prompt fail with nothing to read.
                secureContext = IsSecure(context),
                relyingPartyId = options.RelyingPartyId,
                user = user == null ? null : Describe(user),
            });
        });

        // --- bootstrap ------------------------------------------------------

        api.MapPost("/setup/begin", async (
            HttpContext context, AuthService auth, DisplayNameBody body) => {
                if (await BootstrapRefusal(context, auth) is { } refusal) {
                    return refusal;
                }
                if (Clean(body?.DisplayName) is not { } name) {
                    return Results.BadRequest(new { error = "A display name is required." });
                }
                var (ceremonyId, creation) = auth.BeginRegistration(
                    RegistrationPurpose.Bootstrap, Guid.NewGuid(), name, null, Array.Empty<Credential>());
                return Ceremony(ceremonyId, creation);
            });

        api.MapPost("/setup/complete", async (
            HttpContext context, AuthService auth, RegisterBody body) => {
                if (await BootstrapRefusal(context, auth) is { } refusal) {
                    return refusal;
                }
                return await FinishRegistration(context, auth, body);
            });

        // --- sign in --------------------------------------------------------

        api.MapPost("/signin/begin", (AuthService auth) => {
            var (ceremonyId, options) = auth.BeginAssertion();
            return Ceremony(ceremonyId, options);
        });

        api.MapPost("/signin/complete", async (
            HttpContext context, AuthService auth, AssertBody body) => {
                var result = await auth.CompleteAssertionAsync(
                body?.CeremonyId,
                body == null ? null : JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                    body.Response, _webAuthnJson));
                if (!result.Ok) {
                    return Results.Json(new { error = result.Error }, statusCode: 401);
                }
                await SignIn(context, auth, result.User);
                return Results.Ok(new { user = Describe(result.User) });
            });

        api.MapPost("/signout", async (HttpContext context, AuthService auth) => {
            await auth.SignOutAsync(context.Request.Cookies[AuthService.CookieName]);
            context.Response.Cookies.Delete(AuthService.CookieName);
            return Results.Ok(new { signedOut = true });
        });

        // --- invites --------------------------------------------------------

        // Deliberately uniform: invalid, expired, revoked and already-used all look
        // the same from out here. Telling them apart is a way to learn which codes
        // exist.
        api.MapGet("/invite/{code}", async (AuthService auth, string code) =>
            Results.Ok(new {
                valid = await auth.Store.FindInviteAsync(code) is { } invite
                && invite.IsUsable(DateTime.UtcNow)
            }));

        api.MapPost("/invite/{code}/begin", async (
            AuthService auth, string code, DisplayNameBody body) => {
                var invite = await auth.Store.FindInviteAsync(code);
                if (invite == null || !invite.IsUsable(DateTime.UtcNow)) {
                    return Results.BadRequest(new { error = "This invite isn't valid." });
                }
                if (Clean(body?.DisplayName) is not { } name) {
                    return Results.BadRequest(new { error = "A display name is required." });
                }
                var (ceremonyId, creation) = auth.BeginRegistration(
                    RegistrationPurpose.Invite, Guid.NewGuid(), name, code, Array.Empty<Credential>());
                return Ceremony(ceremonyId, creation);
            });

        api.MapPost("/invite/{code}/complete", async (
            HttpContext context, AuthService auth, string code, RegisterBody body) =>
            await FinishRegistration(context, auth, body));

        // --- your own account ------------------------------------------------

        api.MapGet("/passkeys", async (HttpContext context, AuthService auth) => {
            if (context.CurrentUser() is not { } user) {
                return Results.Json(new { error = "Sign in first." }, statusCode: 401);
            }
            var credentials = await auth.Store.CredentialsForAsync(user.Id);
            return Results.Ok(new {
                passkeys = credentials.Select(c => new {
                    id = c.Id,
                    name = c.Name,
                    createdAt = c.CreatedAt,
                    lastUsedAt = c.LastUsedAt,
                }),
            });
        });

        api.MapPost("/passkeys/begin", async (HttpContext context, AuthService auth) => {
            if (context.CurrentUser() is not { } user) {
                return Results.Json(new { error = "Sign in first." }, statusCode: 401);
            }
            var existing = await auth.Store.CredentialsForAsync(user.Id);
            var (ceremonyId, creation) = auth.BeginRegistration(
                RegistrationPurpose.AddPasskey, user.Id, user.DisplayName, null, existing);
            return Ceremony(ceremonyId, creation);
        });

        api.MapPost("/passkeys/complete", async (
            HttpContext context, AuthService auth, RegisterBody body) => {
                if (context.CurrentUser() == null) {
                    return Results.Json(new { error = "Sign in first." }, statusCode: 401);
                }
                var result = await auth.CompleteRegistrationAsync(
                    body?.CeremonyId, Attestation(body), body?.PasskeyName);
                return result.Ok
                    ? Results.Ok(new { added = true })
                    : Results.BadRequest(new { error = result.Error });
            });

        api.MapDelete("/passkeys/{id}", async (HttpContext context, AuthService auth, string id) => {
            if (context.CurrentUser() is not { } user) {
                return Results.Json(new { error = "Sign in first." }, statusCode: 401);
            }
            return await auth.Store.RemoveCredentialAsync(user.Id, id)
                ? Results.Ok(new { removed = true })
                : Results.BadRequest(new {
                    error = "That is your only passkey. Add another one before removing this.",
                });
        });

        api.MapPut("/profile", async (HttpContext context, AuthService auth, DisplayNameBody body) => {
            if (context.CurrentUser() is not { } user) {
                return Results.Json(new { error = "Sign in first." }, statusCode: 401);
            }
            if (Clean(body?.DisplayName) is not { } name) {
                return Results.BadRequest(new { error = "A display name is required." });
            }
            await auth.Store.RenameUserAsync(user.Id, name);
            return Results.Ok(new { displayName = name });
        });

        MapAdmin(app);
    }

    /// <summary>User and invite management. Admin-only, checked on every route.</summary>
    private static void MapAdmin(IEndpointRouteBuilder app) {
        var api = app.MapGroup("/api");

        api.MapGet("/users", async (HttpContext context, AuthService auth) =>
            context.RequireAdmin() ?? Results.Ok(new {
                users = (await auth.Store.ListUsersAsync()).Select(u => new {
                    id = u.User.Id,
                    displayName = u.User.DisplayName,
                    role = u.User.Role.ToString(),
                    disabled = u.User.Disabled,
                    createdAt = u.User.CreatedAt,
                    lastSeenAt = u.User.LastSeenAt,
                    credentialCount = u.CredentialCount,
                    isYou = u.User.Id == context.CurrentUser()?.Id,
                }),
            }));

        api.MapPut("/users/{id:guid}/role", async (
            HttpContext context, AuthService auth, Guid id, RoleBody body) => {
                if (context.RequireAdmin() is { } refusal) {
                    return refusal;
                }
                if (!Enum.TryParse<UserRole>(body?.Role, out var role)) {
                    return Results.BadRequest(new { error = "Unknown role." });
                }
                return await auth.Store.SetRoleAsync(id, role)
                    ? Results.Ok(new { role = role.ToString() })
                    : Results.BadRequest(new { error = _lastAdmin });
            });

        api.MapPut("/users/{id:guid}/disabled", async (
            HttpContext context, AuthService auth, Guid id, DisabledBody body) => {
                if (context.RequireAdmin() is { } refusal) {
                    return refusal;
                }
                // Locking yourself out is not a thing anyone means to do, and the last-
                // admin guard would not catch it while another admin exists.
                if (id == context.CurrentUser()?.Id && body?.Disabled == true) {
                    return Results.BadRequest(new { error = "You cannot disable your own account." });
                }
                if (!await auth.Store.SetDisabledAsync(id, body?.Disabled ?? false)) {
                    return Results.BadRequest(new { error = _lastAdmin });
                }
                if (body?.Disabled == true) {
                    // Disabling has to end the sessions they already have, not only
                    // their next sign-in.
                    await auth.Store.DeleteSessionsForUserAsync(id);
                }
                return Results.Ok(new { disabled = body?.Disabled ?? false });
            });

        api.MapDelete("/users/{id:guid}", async (HttpContext context, AuthService auth, Guid id) => {
            if (context.RequireAdmin() is { } refusal) {
                return refusal;
            }
            if (id == context.CurrentUser()?.Id) {
                return Results.BadRequest(new { error = "You cannot remove your own account." });
            }
            return await auth.Store.DeleteUserAsync(id)
                ? Results.Ok(new { removed = true })
                : Results.BadRequest(new { error = _lastAdmin });
        });

        api.MapGet("/invites", async (HttpContext context, AuthService auth) =>
            context.RequireAdmin() ?? Results.Ok(new {
                invites = (await auth.Store.ListInvitesAsync()).Select(i => new {
                    code = i.Code,
                    role = i.Role.ToString(),
                    label = i.Label,
                    createdAt = i.CreatedAt,
                    expiresAt = i.ExpiresAt,
                    usedAt = i.UsedAt,
                    revoked = i.Revoked,
                    status = i.UsedAt != null ? "used"
                        : i.Revoked ? "revoked"
                        : i.ExpiresAt <= DateTime.UtcNow ? "expired"
                        : "open",
                }),
            }));

        api.MapPost("/invites", async (
            HttpContext context, AuthService auth, JobsOptions options, InviteBody body) => {
                if (context.RequireAdmin() is { } refusal) {
                    return refusal;
                }
                if (!Enum.TryParse<UserRole>(body?.Role, out var role)) {
                    return Results.BadRequest(new { error = "Unknown role." });
                }
                var invite = await auth.Store.CreateInviteAsync(
                    AuthService.NewInviteCode(), role, Clean(body?.Label), context.CurrentUser()?.Id,
                    DateTime.UtcNow, TimeSpan.FromDays(options.InviteLifetimeDays));
                return Results.Ok(new { code = invite.Code, expiresAt = invite.ExpiresAt });
            });

        api.MapDelete("/invites/{code}", async (HttpContext context, AuthService auth, string code) =>
            context.RequireAdmin()
            ?? (await auth.Store.RevokeInviteAsync(code)
                ? Results.Ok(new { revoked = true })
                : Results.BadRequest(new { error = "That invite has already been used." })));
    }

    private const string _lastAdmin =
        "That would leave the server with no Server Admin. Promote someone else first.";

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// A ceremony payload, serialized by System.Text.Json's *defaults* rather than
    /// through the host's configured options.
    /// <para>
    /// The host adds a global <c>JsonStringEnumConverter</c> so run statuses go over
    /// the wire as their names. A converter in <c>options.Converters</c> outranks a
    /// type's own <c>[JsonConverter]</c> attribute, so that one converter also
    /// caught Fido2's enums, and WebAuthn's <c>"public-key"</c>, <c>"required"</c>
    /// and <c>"none"</c> arrived as <c>"PublicKey"</c>, <c>"Required"</c> and
    /// <c>"None"</c>. Chrome ignored every one of them and refused the ceremony
    /// with "No entry in pubKeyCredParams was of type public-key".
    /// </para>
    /// </summary>
    private static IResult Ceremony(string ceremonyId, object options) =>
        Results.Content(
            "{\"ceremonyId\":" + JsonSerializer.Serialize(ceremonyId)
            + ",\"options\":" + JsonSerializer.Serialize(options, _webAuthnJson) + "}",
            "application/json");

    /// <summary>Deliberately bare: Fido2's own attributes decide the shape.</summary>
    private static readonly JsonSerializerOptions _webAuthnJson = new(JsonSerializerDefaults.Web);

    private static object Describe(User user) => new {
        id = user.Id,
        displayName = user.DisplayName,
        role = user.Role.ToString(),
    };

    private static string Clean(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The bootstrap window, closed two ways. Once a user exists <c>/setup</c> is
    /// gone entirely — 404, not a redirect with an explanation, because a helpful
    /// message is a way to learn that this server exists and is claimed. And it is
    /// loopback-only: on a network-reachable server, whoever reaches it first
    /// otherwise becomes the admin.
    /// </summary>
    private static async Task<IResult> BootstrapRefusal(HttpContext context, AuthService auth) {
        if (await auth.UserCountAsync() > 0) {
            return Results.NotFound();
        }
        var remote = context.Connection.RemoteIpAddress;
        // The *caller's* address, not the configured bind url: behind a proxy those
        // are different, and the bind url would happily call the whole internet
        // local.
        if (remote != null && !IPAddress.IsLoopback(remote)) {
            return Results.Json(new {
                error = "First-run setup has to be done from the machine running the server.",
            }, statusCode: 403);
        }
        return null;
    }

    private static AuthenticatorAttestationRawResponse Attestation(RegisterBody body) =>
        body == null ? null
            : JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
                body.Response, _webAuthnJson);

    private static async Task<IResult> FinishRegistration(
        HttpContext context, AuthService auth, RegisterBody body) {
        var result = await auth.CompleteRegistrationAsync(
            body?.CeremonyId, Attestation(body), body?.PasskeyName);
        if (!result.Ok) {
            return Results.BadRequest(new { error = result.Error });
        }
        await SignIn(context, auth, result.User);
        return Results.Ok(new { user = Describe(result.User) });
    }

    private static async Task SignIn(HttpContext context, AuthService auth, User user) {
        var token = await auth.IssueSessionAsync(user.Id);
        context.Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // Secure only when the request actually arrived over TLS: setting it on
            // a plain-http dev server means the browser drops the cookie and
            // sign-in appears to succeed and then not have happened.
            Secure = IsSecure(context),
            Path = "/",
            MaxAge = TimeSpan.FromDays(context.RequestServices
                .GetService(typeof(JobsOptions)) is JobsOptions o ? o.SessionLifetimeDays : 14),
        });
    }

    private static bool IsSecure(HttpContext context) => context.Request.IsHttps;

    // Bodies. Records rather than anonymous binding so the shapes are named.
    public sealed record DisplayNameBody(string DisplayName);
    // The browser's credential arrives as an opaque JsonElement and is decoded
    // below with Fido2's own shape. Binding it directly would run it through the
    // host's serializer options, whose global enum converter outranks Fido2's
    // type-level ones — and "internal" then fails to parse as a transport, which
    // surfaces as a bare 400 with no message at all.
    public sealed record RegisterBody(string CeremonyId, JsonElement Response, string PasskeyName);
    public sealed record AssertBody(string CeremonyId, JsonElement Response);
    public sealed record RoleBody(string Role);
    public sealed record DisabledBody(bool Disabled);
    public sealed record InviteBody(string Role, string Label);
}
