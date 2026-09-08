namespace ClrKernel.Studio;

/// <summary>
/// A completed authentication, reduced to what every way of proving one has in
/// common: which provider vouched, who it calls this person, and what to show
/// beside it. It is exactly the identifying half of an <see cref="Identity"/> row,
/// which is the point — resolving one to an account is the same lookup whatever
/// produced it.
/// </summary>
/// <param name="Provider">Matches <see cref="Identity.Provider"/>, so
/// <see cref="IdentityProviders.Passkey"/> and not a second spelling of it.</param>
/// <param name="Subject">Opaque to everything but the provider that issued it: a
/// credential id, a directory SID, an OIDC <c>sub</c>.</param>
/// <param name="Label">What to show beside it — a device name, a domain login.</param>
public sealed record ProvenIdentity(string Provider, string Subject, string Label);

/// <summary>
/// A way of proving who somebody is.
///
/// <para>
/// There is one implementation — <see cref="PasskeyProvider"/> — and the interface
/// exists for what it takes <em>out</em> of <see cref="AuthService"/> rather than
/// for what it lets you plug in: the account half no longer knows what a passkey
/// is. It creates accounts, links identities, resolves one to a user and issues
/// sessions, and every step of that is the same for a directory login or an OIDC
/// subject. A second provider supplies its own ceremony, reduces the result to a
/// <see cref="ProvenIdentity"/>, and hands it over.
/// </para>
/// <para>
/// Deliberately not here: the provider's routes. A passkey needs begin/complete
/// pairs, an OIDC provider needs a redirect and a callback, and those shapes are
/// too different to guess at from one implementation — so
/// <c>AuthApi</c> still names them, and the first provider that needs a shape of
/// its own is the one that should decide how they are contributed.
/// </para>
/// </summary>
public interface IAccountProvider {
    /// <summary>
    /// The value written into <see cref="Identity.Provider"/>. Lower-case and
    /// stable: it outlives the code that wrote it.
    /// </summary>
    string Name { get; }

    /// <summary>What a sign-in button calls this. "Passkey", "Windows account".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this provider is configured well enough to be offered. Always true
    /// for passkeys — the browser supplies the authenticator — and the reason the
    /// property exists is the ones that are not: a directory nobody pointed at, an
    /// OIDC client with no secret.
    /// </summary>
    bool IsConfigured { get; }
}
