using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace ClrKernel.Jobs;

/// <summary>A user with the counts the management UI shows beside them.</summary>
public sealed record UserSummary(User User, int CredentialCount);

/// <summary>
/// Accounts, passkeys, invites and sessions.
/// <para>
/// The invariants that must hold whatever the caller does live here rather than in
/// the service above: an invite is redeemable exactly once, a server always has at
/// least one enabled admin, and a user always has at least one passkey. Each is
/// enforced as a conditional write, so two racing requests cannot both win.
/// </para>
/// </summary>
public interface IAuthStore {
    Task<int> UserCountAsync();
    Task<IReadOnlyList<UserSummary>> ListUsersAsync();
    Task<User> FindUserAsync(Guid id);
    /// <summary>
    /// The id is supplied, not generated: it was minted when the passkey ceremony
    /// began, because WebAuthn writes the user handle into the credential itself.
    /// Generating a fresh one here would leave every credential pointing at a user
    /// that does not exist, and assertion fails with nothing readable to say why.
    /// </summary>
    Task<User> CreateUserAsync(Guid id, string displayName, UserRole role);
    Task<bool> RenameUserAsync(Guid id, string displayName);

    /// <summary>False when it would leave no enabled admin.</summary>
    Task<bool> SetRoleAsync(Guid id, UserRole role);

    /// <summary>False when it would leave no enabled admin.</summary>
    Task<bool> SetDisabledAsync(Guid id, bool disabled);

    /// <summary>False when it would leave no enabled admin.</summary>
    Task<bool> DeleteUserAsync(Guid id);

    Task AddCredentialAsync(Credential credential);
    Task<Credential> FindCredentialAsync(string credentialId);
    Task<IReadOnlyList<Credential>> CredentialsForAsync(Guid userId);

    /// <summary>False when it is the user's only passkey — that is a lockout.</summary>
    Task<bool> RemoveCredentialAsync(Guid userId, string credentialId);

    Task RecordCredentialUseAsync(string credentialId, long signCount, DateTime at);

    Task<Invite> CreateInviteAsync(string code, UserRole role, string label, Guid? createdBy,
        DateTime now, TimeSpan lifetime);
    Task<IReadOnlyList<Invite>> ListInvitesAsync();
    Task<Invite> FindInviteAsync(string code);

    /// <summary>
    /// Marks the invite used, but only if it was usable at the moment of the write.
    /// False means someone else got there first, or it had expired or been revoked.
    /// </summary>
    Task<bool> RedeemInviteAsync(string code, Guid userId, DateTime now);
    Task<bool> RevokeInviteAsync(string code);

    Task CreateSessionAsync(AuthSession session);
    Task<(AuthSession Session, User User)> FindSessionAsync(string id, DateTime now);
    Task TouchSessionAsync(string id, DateTime now);
    Task DeleteSessionAsync(string id);
    Task DeleteSessionsForUserAsync(Guid userId);
}

/// <summary>
/// <see cref="IAuthStore"/> over the run-history database — one store, one backup.
/// A fresh context per call, matching <see cref="EfRunStore"/>: this is long-lived
/// and called concurrently, so there is no shared change tracker.
/// </summary>
public sealed class EfAuthStore : IAuthStore {
    private readonly Func<RunsDbContext> _contextFactory;

    public EfAuthStore(Func<RunsDbContext> contextFactory) {
        _contextFactory = contextFactory;
    }

    public async Task<int> UserCountAsync() {
        await using var db = _contextFactory();
        return await db.Users.CountAsync();
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync() {
        await using var db = _contextFactory();
        var rows = await db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { User = u, Count = u.Credentials.Count })
            .ToListAsync();
        return rows.Select(r => new UserSummary(r.User, r.Count)).ToList();
    }

    public async Task<User> FindUserAsync(Guid id) {
        await using var db = _contextFactory();
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<User> CreateUserAsync(Guid id, string displayName, UserRole role) {
        await using var db = _contextFactory();
        var user = new User {
            Id = id,
            DisplayName = displayName,
            Role = role,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<bool> RenameUserAsync(Guid id, string displayName) {
        await using var db = _contextFactory();
        return await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DisplayName, displayName)) > 0;
    }

    public Task<bool> SetRoleAsync(Guid id, UserRole role) =>
        role == UserRole.ServerAdmin
            ? Promote(id)
            : GuardedAdminChange(id, db => db.Users.Where(u => u.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.Role, UserRole.ServerViewer)));

    public Task<bool> SetDisabledAsync(Guid id, bool disabled) =>
        disabled
            ? GuardedAdminChange(id, db => db.Users.Where(u => u.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.Disabled, true)))
            : Enable(id);

    public Task<bool> DeleteUserAsync(Guid id) =>
        GuardedAdminChange(id, db => db.Users.Where(u => u.Id == id).ExecuteDeleteAsync());

    private async Task<bool> Promote(Guid id) {
        await using var db = _contextFactory();
        return await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Role, UserRole.ServerAdmin)) > 0;
    }

    private async Task<bool> Enable(Guid id) {
        await using var db = _contextFactory();
        return await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Disabled, false)) > 0;
    }

    /// <summary>
    /// Applies a change that could remove the last way in, refusing when the target
    /// is the only enabled admin. Read-then-write rather than a single conditional
    /// statement because the condition counts *other* rows; the window is a
    /// single-process server's own request handling, and the failure it would take
    /// is two admins demoting each other in the same millisecond.
    /// </summary>
    private async Task<bool> GuardedAdminChange(Guid id, Func<RunsDbContext, Task<int>> change) {
        await using var db = _contextFactory();
        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (target == null) {
            return false;
        }
        if (target.Role == UserRole.ServerAdmin && !target.Disabled) {
            var others = await db.Users.CountAsync(u =>
                u.Id != id && u.Role == UserRole.ServerAdmin && !u.Disabled);
            if (others == 0) {
                return false;
            }
        }
        return await change(db) > 0;
    }

    public async Task AddCredentialAsync(Credential credential) {
        await using var db = _contextFactory();
        db.Credentials.Add(credential);
        await db.SaveChangesAsync();
    }

    public async Task<Credential> FindCredentialAsync(string credentialId) {
        await using var db = _contextFactory();
        return await db.Credentials.Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Id == credentialId);
    }

    public async Task<IReadOnlyList<Credential>> CredentialsForAsync(Guid userId) {
        await using var db = _contextFactory();
        return await db.Credentials.Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt).ToListAsync();
    }

    public async Task<bool> RemoveCredentialAsync(Guid userId, string credentialId) {
        await using var db = _contextFactory();
        if (await db.Credentials.CountAsync(c => c.UserId == userId) <= 1) {
            return false;
        }
        return await db.Credentials
            .Where(c => c.UserId == userId && c.Id == credentialId)
            .ExecuteDeleteAsync() > 0;
    }

    public async Task RecordCredentialUseAsync(string credentialId, long signCount, DateTime at) {
        await using var db = _contextFactory();
        await db.Credentials.Where(c => c.Id == credentialId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.SignCount, signCount)
                .SetProperty(c => c.LastUsedAt, at));
    }

    public async Task<Invite> CreateInviteAsync(string code, UserRole role, string label,
        Guid? createdBy, DateTime now, TimeSpan lifetime) {
        await using var db = _contextFactory();
        var invite = new Invite {
            Code = code,
            Role = role,
            Label = label,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync();
        return invite;
    }

    public async Task<IReadOnlyList<Invite>> ListInvitesAsync() {
        await using var db = _contextFactory();
        return await db.Invites.OrderByDescending(i => i.CreatedAt).ToListAsync();
    }

    public async Task<Invite> FindInviteAsync(string code) {
        await using var db = _contextFactory();
        return await db.Invites.FirstOrDefaultAsync(i => i.Code == code);
    }

    public async Task<bool> RedeemInviteAsync(string code, Guid userId, DateTime now) {
        await using var db = _contextFactory();
        // The whole test is in the WHERE clause, so two requests racing the same
        // code produce one update and one zero — single use, not nearly single use.
        return await db.Invites
            .Where(i => i.Code == code && !i.Revoked && i.UsedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.UsedAt, now)
                .SetProperty(i => i.UsedBy, userId)) > 0;
    }

    public async Task<bool> RevokeInviteAsync(string code) {
        await using var db = _contextFactory();
        return await db.Invites.Where(i => i.Code == code && i.UsedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.Revoked, true)) > 0;
    }

    public async Task CreateSessionAsync(AuthSession session) {
        await using var db = _contextFactory();
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
    }

    public async Task<(AuthSession Session, User User)> FindSessionAsync(string id, DateTime now) {
        await using var db = _contextFactory();
        var session = await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.ExpiresAt > now);
        if (session == null) {
            return (null, null);
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId);
        // A disabled account keeps its session row but stops being a way in.
        return user is { Disabled: false } ? (session, user) : (null, null);
    }

    public async Task TouchSessionAsync(string id, DateTime now) {
        await using var db = _contextFactory();
        await db.Sessions.Where(s => s.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSeenAt, now));
        var userId = await db.Sessions.Where(s => s.Id == id).Select(s => s.UserId).FirstOrDefaultAsync();
        if (userId != Guid.Empty) {
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.LastSeenAt, now));
        }
    }

    public async Task DeleteSessionAsync(string id) {
        await using var db = _contextFactory();
        await db.Sessions.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    public async Task DeleteSessionsForUserAsync(Guid userId) {
        await using var db = _contextFactory();
        await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
    }
}
