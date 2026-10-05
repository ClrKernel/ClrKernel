using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Windows sign-in from the point a Negotiate handshake has said who somebody is.
/// The handshake needs a Windows host and a browser, so it is not here; everything
/// it hands over to is, and that is where the decisions are — who gets an account,
/// at what role, and which accounts a Windows login can be added to.
/// </summary>
[TestClass]
public class WindowsProviderTest {
    private const string _usersGroup = "S-1-5-21-1-1-1-2001";
    private const string _adminsGroup = "S-1-5-21-1-1-1-2002";

    private static readonly Dictionary<string, (string Sid, string Name)> _directory =
        new(StringComparer.OrdinalIgnoreCase) {
            [@"CORP\Studio Users"] = (_usersGroup, @"CORP\Studio Users"),
            [@"CORP\Studio Admins"] = (_adminsGroup, @"CORP\Studio Admins"),
            [@"CORP\ada"] = ("S-1-5-21-1-1-1-1001", @"CORP\ada"),
            [@"CORP\bob"] = ("S-1-5-21-1-1-1-1002", @"CORP\bob"),
        };

    private string _dir;
    private EfAuthStore _store;
    private AuthService _accounts;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-windows-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options;
        RunsDbContext Factory() => new SqliteRunsDbContext(options);
        using (var db = Factory()) {
            db.Database.Migrate();
        }
        _store = new EfAuthStore(Factory);
        _accounts = new AuthService(_store, new JobsOptions { DataDir = _dir }, NullLogger<AuthService>.Instance);
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_dir);
    }

    private WindowsProvider Provider(string groups = null) => new(
        _accounts, new JobsOptions { DataDir = _dir, WindowsGroups = groups },
        NullLogger<WindowsProvider>.Instance,
        name => _directory.TryGetValue(name, out var found) ? found : null);

    private static WindowsLogin Ada(params string[] groups) =>
        new(_directory[@"CORP\ada"].Sid, @"CORP\ada", groups);

    private static WindowsLogin Bob(params string[] groups) =>
        new(_directory[@"CORP\bob"].Sid, @"CORP\bob", groups);

    private async Task<User> Existing(string name, UserRole role = UserRole.ServerUser) =>
        await _store.CreateUserAsync(Guid.NewGuid(), name.ToLowerInvariant(), name, role);

    [TestMethod]
    public async Task First_run_setup_makes_the_Windows_account_the_server_admin() {
        var result = await Provider().CompleteAsync(WindowsSignInMode.Setup, Ada(), null, null);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(UserRole.ServerAdmin, result.User.Role);
        Assert.AreEqual("ada", result.User.Username, "the handle is the user half of the login");
        var identity = await _store.FindIdentityAsync(IdentityProviders.Windows, Ada().Sid);
        Assert.AreEqual(result.User.Id, identity.UserId, "stored by SID, so a rename does not lose the account");
        Assert.AreEqual(@"CORP\ada", identity.Label);

        var second = await Provider().CompleteAsync(WindowsSignInMode.Setup, Bob(), null, null);
        Assert.IsFalse(second.Ok, "an empty server is claimed once");
    }

    [TestMethod]
    public async Task A_registered_login_signs_in_and_an_unknown_one_with_no_group_is_refused() {
        await Provider().CompleteAsync(WindowsSignInMode.Setup, Ada(), null, null);

        var ada = await Provider().CompleteAsync(WindowsSignInMode.SignIn, Ada(), null, null);
        Assert.IsTrue(ada.Ok, ada.Error);

        var bob = await Provider().CompleteAsync(WindowsSignInMode.SignIn, Bob(), null, null);
        Assert.IsFalse(bob.Ok);
        StringAssert.Contains(bob.Error, @"CORP\bob has no account here");
        Assert.AreEqual(1, await _store.UserCountAsync(), "a refusal creates nothing");
    }

    [TestMethod]
    public async Task A_group_member_gets_an_account_at_the_highest_role_their_groups_give() {
        await Existing("Root", UserRole.ServerAdmin);
        var provider = Provider(@"CORP\Studio Users=ServerUser;CORP\Studio Admins=ServerViewer");

        var bob = await provider.CompleteAsync(WindowsSignInMode.SignIn, Bob(_usersGroup, _adminsGroup), null, null);

        Assert.IsTrue(bob.Ok, bob.Error);
        Assert.AreEqual(UserRole.ServerViewer, bob.User.Role, "Viewer outranks User, whatever the enum's order");
        Assert.AreEqual("bob", bob.User.Username);
        var again = await provider.CompleteAsync(WindowsSignInMode.SignIn, Bob(), null, null);
        Assert.AreEqual(bob.User.Id, again.User.Id,
            "the group is read on the first sign-in only; after that the identity decides");
    }

    [TestMethod]
    public void A_group_Windows_does_not_know_is_skipped_rather_than_provisioning_anyone() {
        var roles = Provider(@"CORP\Nobody=ServerAdmin;CORP\Studio Users=Boss;CORP\Studio Users=ServerUser")
            .GroupRoles;
        Assert.AreEqual(1, roles.Count, "an unknown group and an unknown role are both dropped");
        Assert.AreEqual(UserRole.ServerUser, roles[_usersGroup]);
    }

    [TestMethod]
    public async Task An_invite_for_a_Windows_account_is_redeemed_by_that_account_just_signing_in() {
        await Existing("Root", UserRole.ServerAdmin);
        await _store.CreateInviteAsync("for-bob", UserRole.ServerViewer, null, "Bob Barker", "bobby", null,
            DateTime.UtcNow, TimeSpan.FromDays(1), _directory[@"CORP\bob"].Sid, @"CORP\bob");

        var result = await Provider().CompleteAsync(WindowsSignInMode.SignIn, Bob(), null, null);

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual(UserRole.ServerViewer, result.User.Role);
        Assert.AreEqual("bobby", result.User.Username, "the invite's handle, not a derived one");
        Assert.IsFalse((await _store.FindInviteAsync("for-bob")).IsUsable(DateTime.UtcNow), "spent");
    }

    [TestMethod]
    public async Task An_invite_for_one_Windows_account_refuses_another_and_refuses_a_passkey() {
        await Existing("Root", UserRole.ServerAdmin);
        await _store.CreateInviteAsync("for-bob", UserRole.ServerUser, null, "Bob", "bob", null,
            DateTime.UtcNow, TimeSpan.FromDays(1), _directory[@"CORP\bob"].Sid, @"CORP\bob");

        var ada = await Provider().CompleteAsync(WindowsSignInMode.Invite, Ada(), null, "for-bob");
        Assert.IsFalse(ada.Ok);
        StringAssert.Contains(ada.Error, @"CORP\bob");

        // The passkey path provisions through the same call with no SID.
        var passkey = await _accounts.ProvisionAsync(
            RegistrationPurpose.Invite, Guid.NewGuid(), null, "for-bob", DateTime.UtcNow);
        Assert.IsFalse(passkey.Ok);
        Assert.IsTrue((await _store.FindInviteAsync("for-bob")).IsUsable(DateTime.UtcNow), "still open for Bob");
    }

    [TestMethod]
    public async Task An_ordinary_invite_can_be_redeemed_with_any_Windows_account() {
        await Existing("Root", UserRole.ServerAdmin);
        await _store.CreateInviteAsync("open", UserRole.ServerUser, null, "Ada L", "ada-l", null,
            DateTime.UtcNow, TimeSpan.FromDays(1));

        var result = await Provider().CompleteAsync(WindowsSignInMode.Invite, Ada(), null, "open");

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("ada-l", result.User.Username);
        Assert.IsNotNull(await _store.FindIdentityAsync(IdentityProviders.Windows, Ada().Sid));
    }

    [TestMethod]
    public async Task Windows_is_added_to_a_passkey_account_and_never_to_two_accounts() {
        var ada = await Existing("Ada");
        var bob = await Existing("Bob");

        var linked = await Provider().CompleteAsync(WindowsSignInMode.Link, Ada(), ada, null);
        Assert.IsTrue(linked.Ok, linked.Error);
        var signedIn = await Provider().CompleteAsync(WindowsSignInMode.SignIn, Ada(), null, null);
        Assert.AreEqual(ada.Id, signedIn.User.Id, "linking is what makes Windows sign-in land on this account");

        var stolen = await Provider().CompleteAsync(WindowsSignInMode.Link, Ada(), bob, null);
        Assert.IsFalse(stolen.Ok, "a login names one account");
        Assert.IsFalse((await Provider().CompleteAsync(WindowsSignInMode.Link, Bob(), null, null)).Ok,
            "linking needs somebody signed in");
    }

    [TestMethod]
    public async Task The_last_way_to_sign_in_cannot_be_removed_whichever_kind_it_is() {
        var ada = await Existing("Ada");
        await _store.AddCredentialAsync(new Credential {
            Id = "cred-1",
            UserId = ada.Id,
            PublicKey = new byte[] { 1 },
            Name = "laptop",
            CreatedAt = DateTime.UtcNow,
        });
        await _accounts.LinkIdentityAsync(ada, new ProvenIdentity(IdentityProviders.Passkey, "cred-1", "laptop"), DateTime.UtcNow);

        Assert.IsFalse(await _store.RemoveCredentialAsync(ada.Id, "cred-1"), "the only passkey, nothing else");

        await Provider().CompleteAsync(WindowsSignInMode.Link, Ada(), ada, null);
        Assert.IsTrue(await _store.RemoveCredentialAsync(ada.Id, "cred-1"),
            "with Windows linked, the last passkey can go");

        var windows = (await _store.IdentitiesForAsync(ada.Id)).Single(i => i.Provider == IdentityProviders.Windows);
        Assert.IsFalse(await _store.RemoveIdentityAsync(ada.Id, windows.Id), "and now Windows is the last one");
    }

    [TestMethod]
    public void The_user_half_of_a_login_is_its_handle_seed() {
        Assert.AreEqual("ada", AuthService.UserPart(@"CORP\ada"));
        Assert.AreEqual("ada", AuthService.UserPart("ada@corp.example"));
        Assert.AreEqual("ada", AuthService.UserPart("ada"));
    }
}
