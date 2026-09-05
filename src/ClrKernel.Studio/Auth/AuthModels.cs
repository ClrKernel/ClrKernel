using System;
using System.Collections.Generic;

namespace ClrKernel.Studio;

/// <summary>
/// What an account is across the whole server. Stored by name, so the order here
/// is free to change and new values are free to be appended.
/// </summary>
public enum UserRole {
    /// <summary>Everything: edit, run, promote, manage users, settings and projects.</summary>
    ServerAdmin,

    /// <summary>
    /// Read-only across every project — the auditor's role. Deliberately not the
    /// default for a new account: an account that can read every project makes
    /// per-project grants pointless, because nothing is ever private to a project.
    /// </summary>
    ServerViewer,

    /// <summary>
    /// The baseline: no access to any project at all until granted some. Projects
    /// a Server User has no grant on are not enumerable to them — they do not
    /// appear in the switcher and their ids answer 404, not 403.
    /// </summary>
    ServerUser,
}

/// <summary>
/// What an account is <em>within one project</em>.
/// <para>
/// The order is load-bearing: an effective role is the higher of what the server
/// role implies and any explicit grant, and "higher" is this enum's own order. A
/// grant can raise someone's access on one project and never lower it.
/// </para>
/// </summary>
public enum ProjectRole {
    /// <summary>Read everything in the project, including other people's branches.</summary>
    ProjectViewer,

    /// <summary>Owns a branch here: edits it, runs it, pushes it to test.</summary>
    ProjectMember,

    /// <summary>
    /// Everything within the project: promote to prod, configure the remote,
    /// manage members, prune worktrees. Still cannot write to another user's branch.
    /// </summary>
    ProjectAdmin,
}

/// <summary>One person's explicit grant on one project.</summary>
public sealed class ProjectMembership {
    public string ProjectSlug { get; set; }
    public Guid UserId { get; set; }
    public ProjectRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>How the two tiers of role compose.</summary>
public static class ProjectAccess {
    /// <summary>
    /// The role <paramref name="user"/> actually has on a project, or null for no
    /// access at all. <paramref name="grant"/> is their explicit membership, if any.
    /// </summary>
    public static ProjectRole? Effective(User user, ProjectRole? grant) {
        if (user is null or { Disabled: true }) {
            return null;
        }
        var implied = user.Role switch {
            // A Server Admin is an admin of every project, including ones created
            // after their account was.
            UserRole.ServerAdmin => ProjectRole.ProjectAdmin,
            UserRole.ServerViewer => ProjectRole.ProjectViewer,
            _ => (ProjectRole?)null,
        };
        return implied == null ? grant
            : grant == null ? implied
            : (ProjectRole)Math.Max((int)implied.Value, (int)grant.Value);
    }
}

public sealed class User {
    // Times are UTC DateTime rather than DateTimeOffset, matching the run history
    // beside them: SQLite has no native offset type, so EF cannot order or compare
    // a DateTimeOffset column there at all.
    public Guid Id { get; set; }

    /// <summary>
    /// The handle git and the filesystem know this account by — the branch
    /// <c>user/&lt;username&gt;</c>, the worktree <c>user-&lt;username&gt;</c>, and the
    /// author address on their commits.
    ///
    /// <para>
    /// Unique, lower-case and validated by <see cref="UserName"/>. Separate from
    /// <see cref="DisplayName"/>, which is free text and may repeat, and from
    /// <see cref="Id"/>, which every row points at and which can never change —
    /// it is the WebAuthn user handle inside each of this account's passkeys.
    /// Renaming this moves a branch and a directory, so it is an admin action.
    /// </para>
    /// </summary>
    public string Username { get; set; }

    public string DisplayName { get; set; }
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Kept rather than deleted, so run history keeps naming someone real.</summary>
    public bool Disabled { get; set; }

    public List<Credential> Credentials { get; set; } = new();
    public List<Identity> Identities { get; set; } = new();
}

/// <summary>
/// One passkey. A user may have several — a phone and a laptop — and adding a
/// device is registration against the existing account, never a second account.
/// </summary>
public sealed class Credential {
    /// <summary>The authenticator's credential id, base64url. Unique across users.</summary>
    public string Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; }
    /// <summary>COSE public key as the authenticator returned it.</summary>
    public byte[] PublicKey { get; set; }
    /// <summary>
    /// The authenticator's signature counter. Stored as long rather than uint
    /// because the providers disagree about unsigned columns, and it is compared,
    /// never arithmetic.
    /// </summary>
    public long SignCount { get; set; }
    /// <summary>Comma-separated hints ("internal,hybrid") for the browser's UI.</summary>
    public string Transports { get; set; }
    /// <summary>Identifies the authenticator model. Useful for naming a device.</summary>
    public Guid AaGuid { get; set; }
    /// <summary>What the person calls it. Theirs to set; defaults to the date.</summary>
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// One way an account proves who it is.
///
/// <para>
/// A passkey today; a Windows account or an OIDC subject later. The pair
/// (<see cref="Provider"/>, <see cref="Subject"/>) is what a sign-in presents and
/// is unique across the server — the same directory account cannot be two people —
/// while a person may hold as many as they like, which is what lets somebody sign
/// in with a passkey on a laptop and with the company directory at a desk.
/// </para>
/// <para>
/// Separate from <see cref="Credential"/> rather than a column on it, because a
/// credential is specifically a passkey: a COSE public key and a signature counter,
/// both required, and neither of which a directory account has. A passkey therefore
/// has a row in each — the credential holds the cryptography, the identity holds
/// who it belongs to.
/// </para>
/// </summary>
public sealed class Identity {
    public Guid Id { get; set; }

    /// <summary>The kind of proof: <c>passkey</c> today. Lower-case and stable —
    /// it is written into rows that outlive the code that made them.</summary>
    public string Provider { get; set; }

    /// <summary>
    /// What the provider calls this person: the credential id for a passkey, a
    /// directory SID, an OIDC subject. Opaque here.
    /// </summary>
    public string Subject { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; }

    /// <summary>What to show beside it — a device name, a domain login. Theirs.</summary>
    public string Label { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>Known values for <see cref="Identity.Provider"/>.</summary>
public static class IdentityProviders {
    public const string Passkey = "passkey";
}

/// <summary>
/// A single-use code that creates one account at a fixed role. There is no email
/// in this system, so delivery is manual and the code is the whole mechanism.
/// </summary>
public sealed class Invite {
    /// <summary>Random, URL-safe, and long enough not to be guessable.</summary>
    public string Code { get; set; }
    public UserRole Role { get; set; }
    /// <summary>Free text: who the admin meant it for. Never shown to the invitee.</summary>
    public string Label { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? UsedBy { get; set; }
    public bool Revoked { get; set; }

    /// <summary>
    /// One predicate, so "why can I not use this" has exactly one answer in the
    /// code even though the UI deliberately gives the same message for all of them.
    /// </summary>
    public bool IsUsable(DateTime now) =>
        !Revoked && UsedAt == null && ExpiresAt > now;
}

/// <summary>
/// A signed-in browser. Server-side so sessions can be revoked, and keyed by a
/// hash of the cookie value rather than the value itself — a leaked database read
/// then yields no usable cookies.
/// </summary>
public sealed class AuthSession {
    /// <summary>SHA-256 of the cookie token, base64url.</summary>
    public string Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
