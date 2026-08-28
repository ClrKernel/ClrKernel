using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>A refusal a caller caused and can fix — answered as 400, never 500.</summary>
public sealed class ConnectionException : Exception {
    public ConnectionException(string message) : base(message) { }
}

/// <summary>
/// The saved connections, and the one place a password is written.
/// <para>
/// Connections are server-wide rather than per-project, so this hangs off the data
/// directory beside <c>projects.json</c> and not off any workspace. Reads take the
/// list reference once; writes replace it wholesale under the lock, the same shape
/// <see cref="ProjectRegistry"/> uses and for the same reason — something else is
/// enumerating it from another thread.
/// </para>
/// </summary>
public sealed class ConnectionStore {
    private readonly JobsOptions _options;
    private readonly SecretStore _secrets;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private volatile IReadOnlyList<StoredConnection> _connections;

    /// <summary>
    /// Whether a password typed into the UI can actually be kept.
    /// <para>
    /// <see cref="SecretStore.CanPersist"/> rather than <c>CanStore</c>: the latter is
    /// true whenever <em>any</em> provider can store, and the default chain's first
    /// provider is an in-memory cache. In a container with no keychain that would let
    /// us report a saved password that is gone on the next start.
    /// </para>
    /// </summary>
    public bool CanPersistSecrets { get; }

    public ConnectionStore(JobsOptions options, SecretStore secrets, ILogger<ConnectionStore> logger) {
        _options = options;
        _secrets = secrets ?? new SecretStore();
        _logger = logger;
        _connections = ConnectionsFile.Read(options.DataDir);
        CanPersistSecrets = _secrets.CanPersist;
        if (!CanPersistSecrets) {
            _logger?.LogInformation(
                "No OS credential store is available and {Variable} does not name a file, so " +
                "connection passwords cannot be saved here. Connections take a secret reference " +
                "and the value comes from CLRKERNEL_SECRET_*.",
                FileSecretProvider.PathVariable);
        }
    }

    /// <summary>Every connection, private ones included. For materialization and for
    /// tests; anything answering a request filters with <see cref="VisibleTo"/>.</summary>
    public IReadOnlyList<StoredConnection> All => _connections;

    /// <summary>What one person may see: every shared connection, plus their own.</summary>
    public IReadOnlyList<StoredConnection> VisibleTo(User user) =>
        user == null
            ? Array.Empty<StoredConnection>()
            : _connections.Where(c => c.VisibleTo(user.Id))
                .OrderBy(c => c.Scope).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>The connection by id, or null when it does not exist <em>or</em> the
    /// caller may not see it — those two must not be distinguishable from outside.</summary>
    public StoredConnection Find(string id, User user) =>
        _connections.FirstOrDefault(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)
            && (user == null || c.VisibleTo(user.Id)));

    /// <summary>
    /// Creates or replaces a connection, storing any supplied passwords in the OS
    /// credential store. <paramref name="password"/> and <paramref name="readOnlyPassword"/>
    /// are used and discarded — they are never held on the entry and never written
    /// to the file.
    /// </summary>
    public StoredConnection Save(StoredConnection entry, string password, string readOnlyPassword) {
        if (entry == null) {
            throw new ArgumentNullException(nameof(entry));
        }
        lock (_writeLock) {
            var existing = entry.Id == null
                ? null
                : _connections.FirstOrDefault(c =>
                    string.Equals(c.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            var saved = Normalize(entry, existing);
            StoreSecret(saved.SecretRef, password, saved);
            StoreSecret(saved.ReadOnlySecretRef, readOnlyPassword, saved);

            var next = _connections.Where(c => !ReferenceEquals(c, existing)).ToList();
            next.Add(saved);
            ConnectionsFile.Write(_options.DataDir, next);
            _connections = next;
            // A reference the entry no longer carries — the password was switched to
            // prompt-every-session, or the read-only login was removed — leaves a
            // password behind in the keychain that nothing will ever show or use.
            ForgetOrphan(existing?.SecretRef, saved.SecretRef);
            ForgetOrphan(existing?.ReadOnlySecretRef, saved.ReadOnlySecretRef);
            return saved.Clone();
        }
    }

    /// <summary>Removes a connection and forgets its passwords. The OS store is
    /// best-effort — a stale key is harmless, a kept password is not.</summary>
    public bool Remove(string id) {
        lock (_writeLock) {
            var found = _connections.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (found == null) {
                return false;
            }
            var next = _connections.Where(c => !ReferenceEquals(c, found)).ToList();
            ConnectionsFile.Write(_options.DataDir, next);
            _connections = next;
            Forget(found.SecretRef);
            Forget(found.ReadOnlySecretRef);
            return true;
        }
    }

    /// <summary>Whether a value exists for a reference — the only thing the UI is
    /// ever told about a secret.</summary>
    public bool HasSecret(string secretRef) =>
        !string.IsNullOrEmpty(secretRef) && _secrets.TryResolve(secretRef, out _);

    /// <summary>
    /// Whether this connection has a usable read-only login — both halves of one.
    /// <para>
    /// A password with no user name is opened as user <c>''</c>, and SQL Server
    /// answers that with "Login failed for user ''", which reads like a wrong
    /// password rather than like a field nobody filled in. Asked in one place
    /// because the view that draws the Run button and the route that honours it
    /// have to agree; when they did not, the button said "add a read-only login"
    /// and the route dialled anyway.
    /// </para>
    /// </summary>
    public bool HasReadOnlyLogin(StoredConnection connection) =>
        connection != null
        && !string.IsNullOrWhiteSpace(connection.ReadOnlyUser)
        && HasSecret(connection.ReadOnlySecretRef);

    // --- validation ---------------------------------------------------------

    private StoredConnection Normalize(StoredConnection entry, StoredConnection existing) {
        var name = (entry.Name ?? string.Empty).Trim();
        if (name.Length == 0) {
            throw new ConnectionException("A connection needs a name.");
        }
        if (string.IsNullOrWhiteSpace(entry.Type)) {
            throw new ConnectionException("A connection needs a type.");
        }
        if (entry.Scope == ConnectionScope.Private && entry.OwnerId == null) {
            throw new ConnectionException("A private connection needs an owner.");
        }
        RequireNameFree(name, entry, existing);

        var saved = entry.Clone();
        saved.Name = name;
        saved.Id = existing?.Id ?? entry.Id ?? Guid.NewGuid().ToString("N");
        saved.OwnerId = saved.Scope == ConnectionScope.Shared ? null : saved.OwnerId;
        saved.TimeoutSeconds = Clamp(saved.TimeoutSeconds, 1, 3600, 30);
        saved.RowCap = Clamp(saved.RowCap, 1, 1_000_000, 10_000);
        saved.CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow;
        saved.CreatedBy = existing?.CreatedBy ?? entry.CreatedBy;
        saved.UpdatedAt = DateTime.UtcNow;
        // A "prompt every session" connection has nothing to store, so it must not
        // carry a reference either — a stale ref would resolve to an old password.
        saved.SecretRef = saved.PromptForPassword ? null : saved.SecretRef ?? saved.DefaultSecretRef;
        saved.ReadOnlySecretRef = string.IsNullOrWhiteSpace(saved.ReadOnlyUser)
            ? null
            : saved.ReadOnlySecretRef ?? saved.DefaultReadOnlySecretRef;
        return saved;
    }

    /// <summary>
    /// Shared names are unique server-wide; a private name is unique to its owner and
    /// may not shadow a shared one. The file format lets a <c>.local</c> entry override
    /// a same-named shared one, which is a fine rule for a hand-edited file and a
    /// terrible one here — a notebook naming <c>warehouse</c> would mean a different
    /// database per person.
    /// <para>
    /// Two people's private names may collide, and that is on purpose: refusing would
    /// tell each of them the other's connection exists.
    /// </para>
    /// </summary>
    private void RequireNameFree(string name, StoredConnection entry, StoredConnection existing) {
        var clash = _connections.FirstOrDefault(c =>
            !ReferenceEquals(c, existing)
            && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && (c.Scope == ConnectionScope.Shared
                || (entry.Scope == ConnectionScope.Private && c.OwnerId == entry.OwnerId)));
        if (clash != null) {
            throw new ConnectionException(clash.Scope == ConnectionScope.Shared
                ? $"A shared connection is already called '{name}'."
                : $"You already have a connection called '{name}'.");
        }
    }

    private static int Clamp(int value, int min, int max, int fallback) =>
        value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    // --- secrets ------------------------------------------------------------

    private void StoreSecret(string secretRef, string password, StoredConnection entry) {
        if (string.IsNullOrEmpty(password)) {
            return;
        }
        if (string.IsNullOrEmpty(secretRef)) {
            throw new ConnectionException(entry.PromptForPassword
                ? "This connection prompts for its password, so there is nowhere to save one."
                : "There is no secret reference to save this password under.");
        }
        if (!CanPersistSecrets) {
            throw new ConnectionException(
                "This server has nowhere to keep a password, so one cannot be saved here. " +
                $"Set the {EnvironmentSecretProvider.EnvName(secretRef)} environment variable instead, " +
                $"or point {FileSecretProvider.PathVariable} at a file on this server and try again.");
        }
        _secrets.Store(secretRef, password);
    }

    private void ForgetOrphan(string before, string after) {
        if (!string.IsNullOrEmpty(before)
            && !string.Equals(before, after, StringComparison.Ordinal)) {
            Forget(before);
        }
    }

    private void Forget(string secretRef) {
        if (string.IsNullOrEmpty(secretRef) || !CanPersistSecrets) {
            return;
        }
        try {
            _secrets.Delete(secretRef);
        } catch (Exception e) {
            _logger?.LogWarning("Could not remove the stored secret '{Ref}': {Error}", secretRef, e.Message);
        }
    }
}
