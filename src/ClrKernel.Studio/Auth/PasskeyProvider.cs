using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>Why a registration ceremony was started — it decides what completing it does.</summary>
public enum RegistrationPurpose {
    /// <summary>First run: creates the server's first admin.</summary>
    Bootstrap,

    /// <summary>Redeeming an invite: creates a user at the invite's role.</summary>
    Invite,

    /// <summary>An existing user adding a second device.</summary>
    AddPasskey,
}

/// <summary>
/// A ceremony in flight. Held in memory: short-lived, single use, server-local.
///
/// <para>
/// <see cref="Purpose"/>, <see cref="DisplayName"/> and <see cref="InviteCode"/>
/// are not passkey business — they are what <see cref="AuthService"/> will be
/// asked to provision when this completes. They are parked here because a ceremony
/// is the only thing that spans the two requests, and this table is where a
/// ceremony lives.
/// </para>
/// </summary>
internal sealed record PendingCeremony(
    RegistrationPurpose Purpose,
    Guid UserId,
    string DisplayName,
    string InviteCode,
    CredentialCreateOptions Creation,
    AssertionOptions Assertion,
    DateTime ExpiresAt);

/// <summary>
/// Passkeys: the WebAuthn ceremonies, and the credential rows that back them.
///
/// <para>
/// Verification is <c>Fido2NetLib</c>'s — attestation statements and COSE keys are
/// not something to parse by hand. What lives here is the part that is this
/// application's: the ceremony table, the relying party a ceremony is checked
/// against, and the signature-counter check that catches a cloned authenticator.
/// </para>
/// <para>
/// What is deliberately <em>not</em> here is what happens to the account
/// afterwards. This class does not know what an invite is; it verifies a ceremony,
/// hands <see cref="AuthService"/> a <see cref="ProvenIdentity"/>, and writes the
/// cryptography into its own table.
/// </para>
/// <para>
/// The relying party id and the allowed origins come from configuration, never from
/// the request. Deriving them from the Host header is how you build an app that
/// authenticates against whatever domain an attacker puts in front of it.
/// </para>
/// </summary>
public sealed class PasskeyProvider : IAccountProvider {
    /// <summary>Long enough to use a phone, short enough that a stale one is gone.</summary>
    private static readonly TimeSpan _ceremonyLifetime = TimeSpan.FromMinutes(5);

    private readonly IAuthStore _store;
    private readonly AuthService _accounts;
    private readonly JobsOptions _options;
    private readonly ILogger<PasskeyProvider> _log;
    private readonly Fido2 _fido;
    private readonly ConcurrentDictionary<string, PendingCeremony> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Fido2> _loopbackVerifiers = new(StringComparer.Ordinal);

    public PasskeyProvider(
        IAuthStore store, AuthService accounts, JobsOptions options, ILogger<PasskeyProvider> log) {
        _store = store;
        _accounts = accounts;
        _options = options;
        _log = log;
        _fido = new Fido2(new Fido2Configuration {
            ServerDomain = options.RelyingPartyId,
            ServerName = "ClrKernel Studio",
            Origins = new HashSet<string>(options.Origins, StringComparer.OrdinalIgnoreCase),
        }, metadataService: null);
    }

    public string Name => IdentityProviders.Passkey;

    public string DisplayName => "Passkey";

    /// <summary>
    /// Always. The authenticator is the browser's to supply, so there is nothing
    /// here that an operator could leave unconfigured — which is exactly why this
    /// is the implementation that makes the property look pointless.
    /// </summary>
    public bool IsConfigured => true;

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
            ServerName = "ClrKernel Studio",
            Origins = new HashSet<string>(
                _options.Origins.Append(origin), StringComparer.OrdinalIgnoreCase),
        }, metadataService: null));
    }

    internal static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

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
    /// Finishes a registration: verifies the attestation, has the account half
    /// provision or find the user, then writes the credential and links it.
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
        var provisioned = await _accounts.ProvisionAsync(
            ceremony.Purpose, ceremony.UserId, ceremony.DisplayName, ceremony.InviteCode, now);
        if (!provisioned.Ok) {
            return provisioned;
        }
        var user = provisioned.User;

        var credentialId = Base64Url.Encode(credential.Id);
        var label = string.IsNullOrWhiteSpace(passkeyName)
            ? $"Passkey added {now:yyyy-MM-dd}"
            : passkeyName.Trim();
        await _store.AddCredentialAsync(new Credential {
            Id = credentialId,
            UserId = user.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Transports = credential.Transports == null
                ? null
                : string.Join(',', credential.Transports.Select(t => t.ToString())),
            AaGuid = credential.AaGuid,
            Name = label,
            CreatedAt = now,
        });

        // The identity last, and only once the credential it names exists. The
        // identity row is what sign-in resolves through, so one written first is a
        // row pointing at a passkey nothing can verify. The other order round — a
        // credential with no identity — is the one CompleteAssertionAsync heals.
        await _accounts.LinkIdentityAsync(
            user, new ProvenIdentity(Name, credentialId, label), now);
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

        // Who this is comes from the identity table, not from the credential: that
        // is the one lookup every provider shares, and a passkey is simply the only
        // one so far. The credential still holds the cryptography below.
        var signedIn = await _accounts.SignInAsync(
            new ProvenIdentity(Name, credential.Id, credential.Name), credential);
        if (!signedIn.Ok) {
            return signedIn;
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

        var now = DateTime.UtcNow;
        await _store.RecordCredentialUseAsync(credential.Id, verified.SignCount, now);
        await _accounts.RecordIdentityUseAsync(
            new ProvenIdentity(Name, credential.Id, credential.Name), now);
        return signedIn;
    }

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
