using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The invariants that make the account model safe, against a real SQLite file.
/// These are the ones a UI cannot be trusted to enforce: single-use invites, a
/// server that always has a way in, and a user who always has a way in.
/// </summary>
[TestClass]
public class AuthStoreTest {
    private string _dir;
    private EfAuthStore _store;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-auth-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options;
        RunsDbContext Factory() => new SqliteRunsDbContext(options);
        using (var db = Factory()) {
            db.Database.Migrate();
        }
        _store = new EfAuthStore(Factory);
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_dir);
    }

    private static Credential NewCredential(Guid userId, string id) => new() {
        Id = id,
        UserId = userId,
        PublicKey = new byte[] { 1, 2, 3 },
        SignCount = 0,
        Name = id,
        CreatedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task A_fresh_server_has_no_users() {
        Assert.AreEqual(0, await _store.UserCountAsync());
    }

    [TestMethod]
    public async Task A_user_and_their_passkeys_round_trip() {
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.AddCredentialAsync(NewCredential(user.Id, "cred-a"));
        await _store.AddCredentialAsync(NewCredential(user.Id, "cred-b"));

        var found = await _store.FindCredentialAsync("cred-b");
        Assert.IsNotNull(found);
        Assert.AreEqual(user.Id, found.UserId);
        Assert.AreEqual("Ada", found.User.DisplayName, "the owner rides along with the credential");
        Assert.AreEqual(2, (await _store.CredentialsForAsync(user.Id)).Count);

        var summary = (await _store.ListUsersAsync()).Single();
        Assert.AreEqual(2, summary.CredentialCount);
    }

    /// <summary>Two devices is the point of passkeys; one is a lockout waiting to happen.</summary>
    [TestMethod]
    public async Task The_last_passkey_cannot_be_removed() {
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.AddCredentialAsync(NewCredential(user.Id, "only"));

        Assert.IsFalse(await _store.RemoveCredentialAsync(user.Id, "only"));
        Assert.AreEqual(1, (await _store.CredentialsForAsync(user.Id)).Count);

        await _store.AddCredentialAsync(NewCredential(user.Id, "second"));
        Assert.IsTrue(await _store.RemoveCredentialAsync(user.Id, "only"));
        Assert.AreEqual("second", (await _store.CredentialsForAsync(user.Id)).Single().Id);
    }

    [TestMethod]
    public async Task Removing_a_user_takes_their_passkeys() {
        var admin = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        var viewer = await _store.CreateUserAsync(Guid.NewGuid(), "Bob", UserRole.ServerViewer);
        await _store.AddCredentialAsync(NewCredential(viewer.Id, "bob-1"));

        Assert.IsTrue(await _store.DeleteUserAsync(viewer.Id));
        Assert.IsNull(await _store.FindCredentialAsync("bob-1"),
            "a credential that authenticates as nobody is worse than none");
        Assert.AreEqual(1, await _store.UserCountAsync());
        Assert.IsNotNull(await _store.FindUserAsync(admin.Id));
    }

    [TestMethod]
    public async Task The_last_admin_cannot_be_demoted_disabled_or_removed() {
        var admin = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.CreateUserAsync(Guid.NewGuid(), "Bob", UserRole.ServerViewer);

        Assert.IsFalse(await _store.SetRoleAsync(admin.Id, UserRole.ServerViewer));
        Assert.IsFalse(await _store.SetDisabledAsync(admin.Id, true));
        Assert.IsFalse(await _store.DeleteUserAsync(admin.Id));
        Assert.AreEqual(UserRole.ServerAdmin, (await _store.FindUserAsync(admin.Id)).Role);
    }

    [TestMethod]
    public async Task A_second_admin_unlocks_the_first() {
        var first = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        var second = await _store.CreateUserAsync(Guid.NewGuid(), "Bob", UserRole.ServerViewer);
        Assert.IsTrue(await _store.SetRoleAsync(second.Id, UserRole.ServerAdmin));

        Assert.IsTrue(await _store.SetRoleAsync(first.Id, UserRole.ServerViewer));
        Assert.AreEqual(UserRole.ServerViewer, (await _store.FindUserAsync(first.Id)).Role);
    }

    /// <summary>A disabled admin is not a way in, so it does not count as the last one.</summary>
    [TestMethod]
    public async Task A_disabled_admin_does_not_count_as_cover() {
        var first = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        var second = await _store.CreateUserAsync(Guid.NewGuid(), "Bob", UserRole.ServerAdmin);
        Assert.IsTrue(await _store.SetDisabledAsync(second.Id, true));

        Assert.IsFalse(await _store.SetDisabledAsync(first.Id, true));
        Assert.IsFalse(await _store.DeleteUserAsync(first.Id));
    }

    [TestMethod]
    public async Task An_invite_is_redeemable_exactly_once() {
        var now = DateTime.UtcNow;
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.CreateInviteAsync("code-1", UserRole.ServerViewer, "Bob", user.Id, now,
            TimeSpan.FromDays(7));

        Assert.IsTrue(await _store.RedeemInviteAsync("code-1", user.Id, now));
        Assert.IsFalse(await _store.RedeemInviteAsync("code-1", user.Id, now),
            "single use means the second attempt loses");
        Assert.IsNotNull((await _store.FindInviteAsync("code-1")).UsedAt);
    }

    [TestMethod]
    public async Task An_expired_or_revoked_invite_is_not_redeemable() {
        var now = DateTime.UtcNow;
        await _store.CreateInviteAsync("stale", UserRole.ServerViewer, null, null, now,
            TimeSpan.FromDays(7));
        await _store.CreateInviteAsync("revoked", UserRole.ServerViewer, null, null, now,
            TimeSpan.FromDays(7));
        Assert.IsTrue(await _store.RevokeInviteAsync("revoked"));

        Assert.IsFalse(await _store.RedeemInviteAsync("stale", Guid.NewGuid(), now.AddDays(8)));
        Assert.IsFalse(await _store.RedeemInviteAsync("revoked", Guid.NewGuid(), now));
        Assert.IsFalse(await _store.RedeemInviteAsync("never-existed", Guid.NewGuid(), now));
    }

    [TestMethod]
    public async Task A_used_invite_cannot_be_revoked_after_the_fact() {
        var now = DateTime.UtcNow;
        await _store.CreateInviteAsync("code-1", UserRole.ServerViewer, null, null, now,
            TimeSpan.FromDays(7));
        Assert.IsTrue(await _store.RedeemInviteAsync("code-1", Guid.NewGuid(), now));

        Assert.IsFalse(await _store.RevokeInviteAsync("code-1"),
            "revoking a used invite would misreport what happened to it");
    }

    [TestMethod]
    public async Task A_session_resolves_to_its_user_until_it_expires() {
        var now = DateTime.UtcNow;
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.CreateSessionAsync(new AuthSession {
            Id = "hash-1",
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            LastSeenAt = now,
        });

        Assert.AreEqual(user.Id, (await _store.FindSessionAsync("hash-1", now)).User.Id);
        Assert.IsNull((await _store.FindSessionAsync("hash-1", now.AddDays(2))).User);
        Assert.IsNull((await _store.FindSessionAsync("no-such-session", now)).User);
    }

    /// <summary>Disabling someone has to end their sessions, not just their next sign-in.</summary>
    [TestMethod]
    public async Task A_disabled_user_stops_resolving_immediately() {
        var now = DateTime.UtcNow;
        await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        var bob = await _store.CreateUserAsync(Guid.NewGuid(), "Bob", UserRole.ServerViewer);
        await _store.CreateSessionAsync(new AuthSession {
            Id = "bob-session",
            UserId = bob.Id,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            LastSeenAt = now,
        });

        Assert.IsTrue(await _store.SetDisabledAsync(bob.Id, true));
        Assert.IsNull((await _store.FindSessionAsync("bob-session", now)).User);
    }

    [TestMethod]
    public async Task Signature_counters_are_persisted() {
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        await _store.AddCredentialAsync(NewCredential(user.Id, "cred"));
        var at = DateTime.UtcNow;

        await _store.RecordCredentialUseAsync("cred", 42, at);

        var credential = await _store.FindCredentialAsync("cred");
        Assert.AreEqual(42L, credential.SignCount);
        Assert.IsNotNull(credential.LastUsedAt);
    }
}
