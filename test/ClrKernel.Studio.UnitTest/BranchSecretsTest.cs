using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Secrets kept per branch. The rule is strict: prod sees prod's value and nothing
/// else, so a job promoted without its own secret fails loudly instead of running
/// against test's.
/// </summary>
[TestClass]
public class BranchSecretsTest {
    private string _root;
    private BranchSecrets _secrets;
    private SecretStore _store;

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-branch-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var db = EfRunStore.Sqlite(Path.Combine(_root, "jobs.db"));
        db.Migrate();
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "jobs.db")}").Options;
        // A file store, so the values are really written and read back rather than
        // living in a dictionary this test also owns.
        _store = SecretStore.ForProviders(new FileSecretProvider(Path.Combine(_root, "secrets.json")));
        _secrets = new BranchSecrets(() => new SqliteRunsDbContext(options), _store);
    }

    [TestCleanup]
    public void Cleanup() {
        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // Not a failure.
        }
    }

    /// <summary>The thing the whole design is for.</summary>
    [TestMethod]
    public async Task One_name_on_two_branches_is_two_values() {
        Assert.IsNull(await _secrets.SetAsync("default", "test", "OPENAI", "sk-test", null, "Ada"));
        Assert.IsNull(await _secrets.SetAsync("default", "prod", "OPENAI", "sk-prod", null, "Ada"));

        var test = await _secrets.EnvironmentForAsync("default", "test");
        var prod = await _secrets.EnvironmentForAsync("default", "prod");

        Assert.AreEqual("sk-test", test["CLRKERNEL_SECRET_OPENAI"]);
        Assert.AreEqual("sk-prod", prod["CLRKERNEL_SECRET_OPENAI"]);
    }

    /// <summary>
    /// Strict, not "branch then shared": a kernel started on prod is handed prod's
    /// secrets only, so there is nothing else there for it to fall back to.
    /// </summary>
    [TestMethod]
    public async Task A_branch_without_a_value_is_given_nothing_to_fall_back_on() {
        await _secrets.SetAsync("default", "test", "OPENAI", "sk-test", null, "Ada");
        // Named on prod and *not* valued — the promotion where somebody did half the
        // job. The name has to stay: with the row gone there is nothing a fallback
        // could even attach to, and the test would pass against an implementation
        // that happily borrows test's value.
        await _secrets.SetAsync("default", "prod", "OPENAI", "sk-prod", null, "Ada");
        _store.Delete(BranchSecrets.KeyFor("default", "prod", "OPENAI"));

        var listed = await _secrets.ListAsync("default", "prod");
        Assert.AreEqual(1, listed.Count, "still named on prod");
        Assert.IsFalse(listed[0].IsSet, "and still unset");

        var prod = await _secrets.EnvironmentForAsync("default", "prod");

        Assert.AreEqual(0, prod.Count, "no value, and no borrowing test's");
        CollectionAssert.DoesNotContain(prod.Keys.ToArray(), "CLRKERNEL_SECRET_OPENAI");
    }

    /// <summary>A project is a scope too — two projects may both have an OPENAI.</summary>
    [TestMethod]
    public async Task Projects_do_not_share_either() {
        await _secrets.SetAsync("finance", "test", "OPENAI", "sk-finance", null, "Ada");
        await _secrets.SetAsync("ops", "test", "OPENAI", "sk-ops", null, "Ada");

        Assert.AreEqual("sk-finance",
            (await _secrets.EnvironmentForAsync("finance", "test"))["CLRKERNEL_SECRET_OPENAI"]);
        Assert.AreEqual("sk-ops",
            (await _secrets.EnvironmentForAsync("ops", "test"))["CLRKERNEL_SECRET_OPENAI"]);
    }

    [TestMethod]
    public async Task Listing_says_which_names_have_a_value() {
        await _secrets.SetAsync("default", "test", "OPENAI", "sk", null, "Ada");
        await _secrets.SetAsync("default", "test", "STRIPE", "sk2", null, "Ada");
        // Gone from the store, still named — which is what a shell `security delete`
        // leaves behind, and what the page has to be able to show.
        _store.Delete(BranchSecrets.KeyFor("default", "test", "STRIPE"));

        var listed = await _secrets.ListAsync("default", "test");

        CollectionAssert.AreEqual(new[] { "OPENAI", "STRIPE" }, listed.Select(l => l.Row.Name).ToArray());
        Assert.IsTrue(listed[0].IsSet);
        Assert.IsFalse(listed[1].IsSet, "named but not set is a state the page must show");
        Assert.AreEqual("Ada", listed[0].Row.CreatedByName);
    }

    /// <summary>
    /// The name becomes an environment variable, and EnvName folds every
    /// non-alphanumeric to an underscore — so `my-key` and `my_key` would arrive as
    /// one variable and overwrite each other.
    /// </summary>
    [TestMethod]
    public async Task A_name_that_would_collide_as_a_variable_is_refused() {
        foreach (var bad in new[] { "my-key", "my key", "my.key", "1key", "", null }) {
            Assert.IsNotNull(BranchSecrets.Problem(bad), bad ?? "(null)");
            Assert.IsNotNull(await _secrets.SetAsync("default", "test", bad, "v", null, "Ada"));
        }
        Assert.IsNull(BranchSecrets.Problem("OPENAI_KEY"));
        Assert.IsNull(BranchSecrets.Problem("openai2"));
    }

    [TestMethod]
    public async Task Setting_the_same_name_twice_replaces_the_value() {
        await _secrets.SetAsync("default", "test", "OPENAI", "first", null, "Ada");
        await _secrets.SetAsync("default", "test", "OPENAI", "second", null, "Grace");

        Assert.AreEqual("second",
            (await _secrets.EnvironmentForAsync("default", "test"))["CLRKERNEL_SECRET_OPENAI"]);
        var listed = await _secrets.ListAsync("default", "test");
        Assert.AreEqual(1, listed.Count, "one row, not two");
        Assert.IsNotNull(listed[0].Row.UpdatedAt);
    }
}
