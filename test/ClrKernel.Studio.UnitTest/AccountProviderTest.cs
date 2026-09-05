using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The seam between a way of proving who somebody is and the accounts behind it.
///
/// <para>
/// <see cref="AuthApiTest"/> drives the passkey ceremonies end to end and cannot
/// see this: it would pass just as well if the identity row named the wrong
/// provider, or if sign-in read the account off the credential and never consulted
/// the identity table at all. Both are things the extraction could break silently,
/// so they are asserted here directly.
/// </para>
/// </summary>
[TestClass]
public class AccountProviderTest {
    private string _dir;
    private EfAuthStore _store;
    private AuthService _accounts;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options;
        RunsDbContext Factory() => new SqliteRunsDbContext(options);
        using (var db = Factory()) {
            db.Database.Migrate();
        }
        _store = new EfAuthStore(Factory);
        _accounts = new AuthService(
            _store, new JobsOptions { DataDir = _dir }, NullLogger<AuthService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_dir);
    }

    private static ProvenIdentity Proof(string subject = "cred-1") =>
        new(IdentityProviders.Passkey, subject, "Ada's laptop");

    /// <summary>
    /// The name a provider writes into its identity rows is the name it looks them
    /// up by. A provider that answered `Name` with one spelling and stored another
    /// would register accounts nobody could ever sign in as.
    /// </summary>
    [TestMethod]
    public async Task What_a_provider_calls_itself_is_what_it_stores() {
        var passkeys = new PasskeyProvider(
            _store, _accounts, new JobsOptions { DataDir = _dir },
            NullLogger<PasskeyProvider>.Instance);
        Assert.AreEqual(IdentityProviders.Passkey, passkeys.Name);

        var user = await _store.CreateUserAsync(Guid.NewGuid(), "ada", "Ada", UserRole.ServerAdmin);
        await _accounts.LinkIdentityAsync(
            user, new ProvenIdentity(passkeys.Name, "cred-1", "laptop"), DateTime.UtcNow);

        var found = await _store.FindIdentityAsync(passkeys.Name, "cred-1");
        Assert.IsNotNull(found, "the row a provider writes is the row it reads back");
        Assert.AreEqual(user.Id, found.UserId);
    }

    /// <summary>Bootstrap and invite policy, reached without a ceremony.</summary>
    [TestMethod]
    public async Task Provisioning_is_the_account_half_and_needs_no_passkey() {
        var first = await _accounts.ProvisionAsync(
            RegistrationPurpose.Bootstrap, Guid.NewGuid(), "Ada Lovelace", null, DateTime.UtcNow);
        Assert.IsTrue(first.Ok, first.Error);
        Assert.AreEqual(UserRole.ServerAdmin, first.User.Role);
        Assert.AreEqual("ada-lovelace", first.User.Username, "a handle is derived here, and only here");

        // And the empty-server window closes behind it.
        var second = await _accounts.ProvisionAsync(
            RegistrationPurpose.Bootstrap, Guid.NewGuid(), "Somebody Else", null, DateTime.UtcNow);
        Assert.IsFalse(second.Ok);
        StringAssert.Contains(second.Error, "already has an account");

        // An invite brings its own name and handle, and is spent by using it.
        var now = DateTime.UtcNow;
        await _store.CreateInviteAsync(
            "code", UserRole.ServerUser, null, "Grace Hopper", "grace", first.User.Id, now,
            TimeSpan.FromDays(7));
        var invited = await _accounts.ProvisionAsync(
            RegistrationPurpose.Invite, Guid.NewGuid(), "ignored", "code", now);
        Assert.IsTrue(invited.Ok, invited.Error);
        Assert.AreEqual("grace", invited.User.Username);
        Assert.AreEqual("Grace Hopper", invited.User.DisplayName);
        Assert.AreEqual(UserRole.ServerUser, invited.User.Role);

        var again = await _accounts.ProvisionAsync(
            RegistrationPurpose.Invite, Guid.NewGuid(), null, "code", now);
        Assert.IsFalse(again.Ok, "single use");
    }

    /// <summary>
    /// Sign-in resolves through the identity table. The credential is only a
    /// fallback for one that predates identities — so a subject nothing has ever
    /// linked is refused rather than quietly accepted.
    /// </summary>
    [TestMethod]
    public async Task Signing_in_goes_through_the_identity_and_not_around_it() {
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "ada", "Ada", UserRole.ServerAdmin);

        var stranger = await _accounts.SignInAsync(Proof("never-linked"));
        Assert.IsFalse(stranger.Ok, "an unlinked subject is nobody");

        await _accounts.LinkIdentityAsync(user, Proof(), DateTime.UtcNow);
        var known = await _accounts.SignInAsync(Proof());
        Assert.IsTrue(known.Ok, known.Error);
        Assert.AreEqual(user.Id, known.User.Id);

        // A different provider with the same subject is a different identity: the
        // pair is the key, not the subject on its own.
        var elsewhere = await _accounts.SignInAsync(new ProvenIdentity("oidc", "cred-1", null));
        Assert.IsFalse(elsewhere.Ok, "the provider is part of who this is");
    }

    /// <summary>
    /// A passkey from before identities existed, on a database that missed the
    /// backfill. The credential already proves who it is, so the row is written
    /// rather than the person locked out of their own server.
    /// </summary>
    [TestMethod]
    public async Task A_credential_with_no_identity_is_healed_rather_than_refused() {
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "ada", "Ada", UserRole.ServerAdmin);
        await _store.AddCredentialAsync(new Credential {
            Id = "cred-1",
            UserId = user.Id,
            PublicKey = new byte[] { 1, 2, 3 },
            Name = "Ada's laptop",
            CreatedAt = DateTime.UtcNow,
        });
        // Read back rather than reused: the fallback is what sign-in gets from the
        // store, with its User navigation loaded, and that is the shape under test.
        var credential = await _store.FindCredentialAsync("cred-1");

        Assert.IsNull(await _store.FindIdentityAsync(IdentityProviders.Passkey, "cred-1"));
        var result = await _accounts.SignInAsync(Proof(), credential);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(user.Id, result.User.Id);
        var healed = await _store.FindIdentityAsync(IdentityProviders.Passkey, "cred-1");
        Assert.IsNotNull(healed, "the missing row was written, not merely worked around");
        Assert.AreEqual("Ada's laptop", healed.Label);
    }

    [TestMethod]
    public async Task A_disabled_account_cannot_sign_in_however_it_proved_itself() {
        // Not an admin: the store refuses to disable the last one, and that guard
        // has nothing to do with what this test is about.
        await _store.CreateUserAsync(Guid.NewGuid(), "boss", "Boss", UserRole.ServerAdmin);
        var user = await _store.CreateUserAsync(Guid.NewGuid(), "ada", "Ada", UserRole.ServerUser);
        await _accounts.LinkIdentityAsync(user, Proof(), DateTime.UtcNow);
        Assert.IsTrue(await _store.SetDisabledAsync(user.Id, true));

        var result = await _accounts.SignInAsync(Proof());
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "disabled");
    }
}
