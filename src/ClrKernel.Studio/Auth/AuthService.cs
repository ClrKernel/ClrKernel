using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// base64url, the encoding every WebAuthn value travels in. `System.Buffers.Text`
/// grew this in .NET 9 and this targets net8.0, so it is twelve lines here rather
/// than a framework bump for twelve lines.
/// </summary>
internal static class Base64Url {
    public static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value) {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

/// <summary>The outcome of a completed ceremony: a user, or a reason there isn't one.</summary>
public sealed record AuthResult(User User, string Error) {
    public bool Ok => User != null;
    public static AuthResult Fail(string error) => new(null, error);
    public static AuthResult Success(User user) => new(user, null);
}

/// <summary>
/// Accounts and sessions — everything about signing in that is not about <em>how</em>
/// somebody proved who they are.
///
/// <para>
/// It creates an account (first run, or an invite), links an identity to one,
/// resolves an identity back to a user, and issues the session cookie. None of that
/// mentions a passkey: the proof arrives as a <see cref="ProvenIdentity"/>, which a
/// directory login or an OIDC subject reduces to just as well. The WebAuthn half
/// lives in <see cref="PasskeyProvider"/>.
/// </para>
/// </summary>
public sealed class AuthService {
    public const string CookieName = "clrkernel_studio_session";

    private readonly IAuthStore _store;
    private readonly JobsOptions _options;
    private readonly ILogger<AuthService> _log;

    public AuthService(IAuthStore store, JobsOptions options, ILogger<AuthService> log) {
        _store = store;
        _options = options;
        _log = log;
    }

    public IAuthStore Store => _store;

    public Task<int> UserCountAsync() => _store.UserCountAsync();

    // --- provisioning ------------------------------------------------------

    /// <summary>
    /// The account a completed registration belongs to: found, for somebody adding
    /// a second credential, and otherwise created.
    ///
    /// <para>
    /// Every rule about <em>which</em> account gets made is here rather than in the
    /// provider that ran the ceremony: an invite's role and handle, the empty-server
    /// window, the collision re-check. A provider knows how to prove somebody is who
    /// they say; it has no business knowing what an invite is.
    /// </para>
    /// </summary>
    public async Task<AuthResult> ProvisionAsync(
        RegistrationPurpose purpose, Guid userId, string ceremonyDisplayName, string inviteCode,
        DateTime now) {
        if (purpose == RegistrationPurpose.AddPasskey) {
            var existing = await _store.FindUserAsync(userId);
            return existing == null
                ? AuthResult.Fail("That account no longer exists.")
                : AuthResult.Success(existing);
        }

        var role = UserRole.ServerAdmin;
        var displayName = ceremonyDisplayName;
        string username = null;
        if (purpose == RegistrationPurpose.Invite) {
            var invite = await _store.FindInviteAsync(inviteCode);
            if (invite == null || string.IsNullOrEmpty(invite.Username)) {
                return AuthResult.Fail("This invite isn't valid.");
            }
            // Last check before the row is written. The API checks it when the page
            // loads and again as the ceremony begins; between then and now an admin
            // can still rename somebody onto this handle, and users.username is
            // unique — so without this the failure is a 500 with a passkey already
            // created.
            if ((await _store.UsernamesAsync()).Contains(invite.Username, StringComparer.OrdinalIgnoreCase)) {
                return AuthResult.Fail(
                    $"The username on this invite ('{invite.Username}') has since been taken. "
                    + "Ask for a new one.");
            }
            // Spent *before* the account exists, so a race that loses the redeem
            // creates no user at all rather than a user with no invite.
            if (!await _store.RedeemInviteAsync(inviteCode, userId, now)) {
                return AuthResult.Fail("This invite isn't valid.");
            }
            role = invite.Role;
            displayName = invite.DisplayName;
            username = invite.Username;
        } else if (await _store.UserCountAsync() > 0) {
            // Two people racing the empty-server window; the second is not an admin
            // by accident.
            return AuthResult.Fail("This server already has an account.");
        }
        // First-run setup only, and the one place a handle is still derived: an
        // empty server has no admin to fill in a form, and nothing to collide with
        // either. Every other account gets its handle from its invite.
        username ??= UserName.Unique(
            UserName.Suggest(displayName), await _store.UsernamesAsync());
        return AuthResult.Success(
            await _store.CreateUserAsync(userId, username, displayName, role));
    }

    /// <summary>
    /// Records that <paramref name="proven"/> now names this account.
    ///
    /// <para>
    /// Written when the credential is registered rather than derived at sign-in, so
    /// the two cannot drift. Callers link <em>after</em> whatever the identity
    /// points at exists — an identity is what sign-in resolves through, so one
    /// written first names something nothing can verify.
    /// </para>
    /// </summary>
    public Task LinkIdentityAsync(User user, ProvenIdentity proven, DateTime now) =>
        _store.AddIdentityAsync(new Identity {
            Id = Guid.NewGuid(),
            Provider = proven.Provider,
            Subject = proven.Subject,
            UserId = user.Id,
            Label = proven.Label,
            CreatedAt = now,
        });

    // --- sign-in -----------------------------------------------------------

    /// <summary>
    /// The account a proof belongs to, or why it is no good. The one lookup every
    /// provider shares.
    /// </summary>
    /// <param name="fallback">
    /// Where the account can also be read from when the identity row is missing —
    /// a passkey's own credential row, which already proves who this is. Healing
    /// beats refusing: the alternative is locking somebody out of their own server
    /// over a bookkeeping row. A provider with no second source passes null and
    /// gets a refusal instead.
    /// </param>
    public async Task<AuthResult> SignInAsync(ProvenIdentity proven, Credential fallback = null) {
        var identity = await _store.FindIdentityAsync(proven.Provider, proven.Subject);
        if (identity == null) {
            if (fallback?.User == null) {
                return AuthResult.Fail("That sign-in is not registered here.");
            }
            await LinkIdentityAsync(
                fallback.User, proven with { Label = fallback.Name }, fallback.CreatedAt);
            _log.LogWarning(
                "{Provider} {Subject} had no identity row; added one.",
                proven.Provider, proven.Subject);
            identity = await _store.FindIdentityAsync(proven.Provider, proven.Subject);
        }

        var user = identity?.User ?? fallback?.User;
        if (user == null) {
            return AuthResult.Fail("That sign-in is not registered here.");
        }
        return user.Disabled ? AuthResult.Fail("That account is disabled.") : AuthResult.Success(user);
    }

    /// <summary>Notes that this identity was just used, for the "last seen" column.</summary>
    public async Task RecordIdentityUseAsync(ProvenIdentity proven, DateTime now) {
        if (await _store.FindIdentityAsync(proven.Provider, proven.Subject) is { } identity) {
            await _store.RecordIdentityUseAsync(identity.Id, now);
        }
    }

    // --- sessions ----------------------------------------------------------

    /// <summary>
    /// Issues a session and returns the cookie value. Only the hash is stored, so
    /// the database never holds anything that can be presented as a cookie.
    /// </summary>
    public async Task<string> IssueSessionAsync(Guid userId) {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        await _store.CreateSessionAsync(new AuthSession {
            Id = HashToken(token),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.SessionLifetimeDays),
            LastSeenAt = now,
        });
        return token;
    }

    public Task<(AuthSession Session, User User)> ResolveSessionAsync(string token) =>
        string.IsNullOrEmpty(token)
            ? Task.FromResult<(AuthSession, User)>((null, null))
            : _store.FindSessionAsync(HashToken(token), DateTime.UtcNow);

    public Task TouchSessionAsync(string token) =>
        _store.TouchSessionAsync(HashToken(token), DateTime.UtcNow);

    public Task SignOutAsync(string token) =>
        string.IsNullOrEmpty(token) ? Task.CompletedTask : _store.DeleteSessionAsync(HashToken(token));

    internal static string HashToken(string token) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A URL-safe invite code with 160 bits behind it — not guessable.</summary>
    public static string NewInviteCode() => Base64Url.Encode(RandomNumberGenerator.GetBytes(20));
}
