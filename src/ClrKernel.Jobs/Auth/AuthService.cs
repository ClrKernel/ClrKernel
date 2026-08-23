using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

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

/// <summary>Why a registration ceremony was started — it decides what completing it does.</summary>
public enum RegistrationPurpose {
    /// <summary>First run: creates the server's first admin.</summary>
    Bootstrap,

    /// <summary>Redeeming an invite: creates a user at the invite's role.</summary>
    Invite,

    /// <summary>An existing user adding a second device.</summary>
    AddPasskey,
}

/// <summary>A ceremony in flight. Held in memory: short-lived, single use, server-local.</summary>
internal sealed record PendingCeremony(
    RegistrationPurpose Purpose,
    Guid UserId,
    string DisplayName,
    string InviteCode,
    CredentialCreateOptions Creation,
    AssertionOptions Assertion,
    DateTime ExpiresAt);

/// <summary>The outcome of a completed ceremony: a user, or a reason there isn't one.</summary>
public sealed record AuthResult(User User, string Error) {
    public bool Ok => User != null;
    public static AuthResult Fail(string error) => new(null, error);
    public static AuthResult Success(User user) => new(user, null);
}

/// <summary>
/// Passkey ceremonies and sessions.
/// <para>
/// Verification is <c>Fido2NetLib</c>'s: attestation statements and COSE keys are
/// not something to parse by hand. What lives here is the part that is this
/// application's — which ceremony creates which kind of account, what a session is,
/// and the signature-counter check that catches a cloned authenticator.
/// </para>
/// <para>
/// The relying party id and the allowed origins come from configuration, never from
/// the request. Deriving them from the Host header is how you build an app that
/// authenticates against whatever domain an attacker puts in front of it.
/// </para>
/// </summary>
public sealed class AuthService {
    /// <summary>Long enough to use a phone, short enough that a stale one is gone.</summary>
    private static readonly TimeSpan _ceremonyLifetime = TimeSpan.FromMinutes(5);

    public const string CookieName = "clrkernel_jobs_session";

    private readonly IAuthStore _store;
    private readonly JobsOptions _options;
    private readonly ILogger<AuthService> _log;
    private readonly Fido2 _fido;
    private readonly ConcurrentDictionary<string, PendingCeremony> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Fido2> _loopbackVerifiers = new(StringComparer.Ordinal);

    public AuthService(IAuthStore store, JobsOptions options, ILogger<AuthService> log) {
        _store = store;
        _options = options;
        _log = log;
        _fido = new Fido2(new Fido2Configuration {
            ServerDomain = options.RelyingPartyId,
            ServerName = "ClrKernel Jobs",
            Origins = new HashSet<string>(options.Origins, StringComparer.OrdinalIgnoreCase),
        }, metadataService: null);
    }

    public IAuthStore Store => _store;

    /// <summary>
    /// The verifier to check a ceremony against, given the origin the browser
    /// actually sent.
    /// <para>
    /// Normally this is the one built from configuration. The exception is the
    /// development loop: Vite serves the app on :5173 and proxies <c>/api</c> to
    /// the server on :5000, so the browser's origin is not the bind url and the
    /// ceremony is rejected — which is what happens if you follow this repo's own
    /// dev instructions.
    /// </para>
    /// <para>
    /// A WebAuthn relying party is a *domain*; the port is not part of it, and the
    /// browser already scopes the credential accordingly. So when the relying party
    /// is <c>localhost</c> — which is a development configuration by definition,
    /// and whose passkeys are documented as throwaway — another loopback port is
    /// the same relying party and refusing it is stricter than WebAuthn itself.
    /// Anything else, including a real hostname on loopback, still has to be in the
    /// configured list.
    /// </para>
    /// </summary>
    private Fido2 VerifierFor(string requestOrigin) {
        if (requestOrigin == null
            || _options.RelyingPartyId != "localhost"
            || _options.Origins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase)
            || !IsLoopbackOrigin(requestOrigin)) {
            return _fido;
        }
        return _loopbackVerifiers.GetOrAdd(requestOrigin, origin => new Fido2(new Fido2Configuration {
            ServerDomain = _options.RelyingPartyId,
            ServerName = "ClrKernel Jobs",
            Origins = new HashSet<string>(
                _options.Origins.Append(origin), StringComparer.OrdinalIgnoreCase),
        }, metadataService: null));
    }

    internal static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    public Task<int> UserCountAsync() => _store.UserCountAsync();

    // --- registration ------------------------------------------------------

    /// <summary>
    /// Starts a registration. `existing` is the user's current credentials, which
    /// become excludeCredentials: an authenticator that already holds a passkey for
    /// this account then declines rather than silently making a second one.
    /// </summary>
    public (string CeremonyId, CredentialCreateOptions Options) BeginRegistration(
        RegistrationPurpose purpose, Guid userId, string displayName, string inviteCode,
        IReadOnlyList<Credential> existing) {
        var options = _fido.RequestNewCredential(new RequestNewCredentialParams {
            User = new Fido2User {
                Id = userId.ToByteArray(),
                // There is no username in this system; the display name is all
                // there is, and it is what the browser shows in the passkey list.
                Name = displayName,
                DisplayName = displayName,
            },
            ExcludeCredentials = existing
                .Select(c => new PublicKeyCredentialDescriptor(Base64Url.Decode(c.Id)))
                .ToList(),
            AuthenticatorSelection = new AuthenticatorSelection {
                // Discoverable, so signing in is one button and no username field.
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            // Nothing here consults an attestation metadata service, so asking for
            // an attestation statement would be collecting evidence we never read.
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return (Remember(new PendingCeremony(
            purpose, userId, displayName, inviteCode, options, null,
            DateTime.UtcNow + _ceremonyLifetime)), options);
    }

    /// <summary>
    /// Finishes a registration: verifies the attestation, then creates the account
    /// (bootstrap, invite) or attaches the passkey to the existing one.
    /// </summary>
    public async Task<AuthResult> CompleteRegistrationAsync(
        string ceremonyId, AuthenticatorAttestationRawResponse response, string passkeyName,
        string requestOrigin = null) {
        if (Claim(ceremonyId) is not { Creation: not null } ceremony) {
            return AuthResult.Fail("That registration expired. Start again.");
        }

        RegisteredPublicKeyCredential credential;
        try {
            credential = await VerifierFor(requestOrigin).MakeNewCredentialAsync(new MakeNewCredentialParams {
                AttestationResponse = response,
                OriginalOptions = ceremony.Creation,
                IsCredentialIdUniqueToUserCallback = async (parameters, _) =>
                    await _store.FindCredentialAsync(Base64Url.Encode(parameters.CredentialId)) == null,
            });
        } catch (Exception e) {
            _log.LogWarning(e, "Passkey registration rejected");
            return AuthResult.Fail("That passkey could not be registered.");
        }

        var now = DateTime.UtcNow;
        User user;
        if (ceremony.Purpose == RegistrationPurpose.AddPasskey) {
            user = await _store.FindUserAsync(ceremony.UserId);
            if (user == null) {
                return AuthResult.Fail("That account no longer exists.");
            }
        } else {
            // The invite is spent *before* the account exists, so a race that loses
            // the redeem creates no user at all rather than a user with no invite.
            var role = UserRole.ServerAdmin;
            if (ceremony.Purpose == RegistrationPurpose.Invite) {
                var invite = await _store.FindInviteAsync(ceremony.InviteCode);
                if (invite == null || !await _store.RedeemInviteAsync(
                        ceremony.InviteCode, ceremony.UserId, now)) {
                    return AuthResult.Fail("This invite isn't valid.");
                }
                role = invite.Role;
            } else if (await _store.UserCountAsync() > 0) {
                // Two people racing the empty-server window; the second is not an
                // admin by accident.
                return AuthResult.Fail("This server already has an account.");
            }
            user = await _store.CreateUserAsync(ceremony.UserId, ceremony.DisplayName, role);
        }

        await _store.AddCredentialAsync(new Credential {
            Id = Base64Url.Encode(credential.Id),
            UserId = user.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Transports = credential.Transports == null
                ? null
                : string.Join(',', credential.Transports.Select(t => t.ToString())),
            AaGuid = credential.AaGuid,
            Name = string.IsNullOrWhiteSpace(passkeyName)
                ? $"Passkey added {now:yyyy-MM-dd}"
                : passkeyName.Trim(),
            CreatedAt = now,
        });
        return AuthResult.Success(user);
    }

    // --- sign-in -----------------------------------------------------------

    /// <summary>
    /// Starts a sign-in. No allow-list: the credentials are discoverable, so the
    /// authenticator offers what it holds and the server learns who it is from the
    /// assertion. That is what removes the username field.
    /// </summary>
    public (string CeremonyId, AssertionOptions Options) BeginAssertion() {
        var options = _fido.GetAssertionOptions(new GetAssertionOptionsParams {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
        return (Remember(new PendingCeremony(
            RegistrationPurpose.AddPasskey, Guid.Empty, null, null, null, options,
            DateTime.UtcNow + _ceremonyLifetime)), options);
    }

    public async Task<AuthResult> CompleteAssertionAsync(
        string ceremonyId, AuthenticatorAssertionRawResponse response, string requestOrigin = null) {
        if (Claim(ceremonyId) is not { Assertion: not null } ceremony) {
            return AuthResult.Fail("That sign-in expired. Try again.");
        }

        // `Id` arrives base64url-encoded, which is exactly how credentials are keyed.
        var credential = await _store.FindCredentialAsync(response.Id);
        if (credential?.User == null) {
            return AuthResult.Fail("That passkey is not registered here.");
        }
        if (credential.User.Disabled) {
            return AuthResult.Fail("That account is disabled.");
        }

        VerifyAssertionResult verified;
        try {
            verified = await VerifierFor(requestOrigin).MakeAssertionAsync(new MakeAssertionParams {
                AssertionResponse = response,
                OriginalOptions = ceremony.Assertion,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (parameters, _) =>
                    Task.FromResult(new Guid(parameters.UserHandle) == credential.UserId),
            });
        } catch (Exception e) {
            _log.LogWarning(e, "Passkey assertion rejected for credential {Credential}", credential.Id);
            return AuthResult.Fail("That passkey could not be verified.");
        }

        // A counter that has not advanced means the same signature could be
        // replayed, or the authenticator has been cloned. Authenticators that do
        // not implement counters report zero forever, which is allowed — the check
        // only bites once a credential has ever reported a non-zero count.
        if (credential.SignCount > 0 && verified.SignCount <= credential.SignCount) {
            _log.LogError(
                "Rejecting assertion for credential {Credential} (user {User}): signature counter " +
                "went from {Stored} to {Presented}. This is what a cloned authenticator looks like.",
                credential.Id, credential.UserId, credential.SignCount, verified.SignCount);
            return AuthResult.Fail("That passkey could not be verified.");
        }

        await _store.RecordCredentialUseAsync(credential.Id, verified.SignCount, DateTime.UtcNow);
        return AuthResult.Success(credential.User);
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

    // --- ceremony bookkeeping ---------------------------------------------

    private string Remember(PendingCeremony ceremony) {
        Sweep();
        var id = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        _pending[id] = ceremony;
        return id;
    }

    /// <summary>Takes the ceremony out of the table — single use, whatever happens next.</summary>
    private PendingCeremony Claim(string id) {
        if (id == null || !_pending.TryRemove(id, out var ceremony)) {
            return null;
        }
        return ceremony.ExpiresAt > DateTime.UtcNow ? ceremony : null;
    }

    private void Sweep() {
        if (_pending.Count < 64) {
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var (id, ceremony) in _pending) {
            if (ceremony.ExpiresAt <= now) {
                _pending.TryRemove(id, out _);
            }
        }
    }
}
