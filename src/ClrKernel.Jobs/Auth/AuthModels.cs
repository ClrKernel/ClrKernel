using System;
using System.Collections.Generic;

namespace ClrKernel.Jobs;

/// <summary>
/// Server-wide, not per notebook or per job. Two is the whole set on purpose: the
/// interesting boundary is "may execute arbitrary code on this machine", and every
/// finer distinction anyone might want still sits on the same side of it.
/// </summary>
public enum UserRole {
    /// <summary>Everything: edit, run, promote, manage users and settings.</summary>
    ServerAdmin,

    /// <summary>Read-only. May look at anything; may change or run nothing.</summary>
    ServerViewer,
}

public sealed class User {
    // Times are UTC DateTime rather than DateTimeOffset, matching the run history
    // beside them: SQLite has no native offset type, so EF cannot order or compare
    // a DateTimeOffset column there at all.
    public Guid Id { get; set; }
    public string DisplayName { get; set; }
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    /// <summary>Kept rather than deleted, so run history keeps naming someone real.</summary>
    public bool Disabled { get; set; }

    public List<Credential> Credentials { get; set; } = new();
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
