using System;
using System.IO;
using System.Linq;
using ClrKernel.Core.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The writable secret store a container has. Without it a self-hosted server can
/// only take passwords as <c>CLRKERNEL_SECRET_*</c> variables, so adding one
/// connection means editing the deployment and restarting.
/// </summary>
[TestClass]
public class FileSecretProviderTest {
    private string _dir;
    private string _path;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-secretfile-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "secrets.json");
    }

    [TestCleanup]
    public void Cleanup() {
        if (Directory.Exists(_dir)) {
            TempDirectory.Delete(_dir);
        }
    }

    [TestMethod]
    public void A_stored_secret_survives_a_new_process() {
        new FileSecretProvider(_path).Set("sql:analytics", "hunter2");

        // A second instance reads no state from the first — this is what the
        // separately-spawned kernel process does.
        Assert.IsTrue(new FileSecretProvider(_path).TryGet("sql:analytics", out var secret));
        Assert.AreEqual("hunter2", secret);

        new FileSecretProvider(_path).Delete("sql:analytics");
        Assert.IsFalse(new FileSecretProvider(_path).TryGet("sql:analytics", out _));
    }

    /// <summary>
    /// A read is not cached, deliberately: the kernel runs as its own process and
    /// would otherwise keep using a password the web app has already replaced.
    /// </summary>
    [TestMethod]
    public void A_replaced_secret_is_seen_by_a_reader_that_already_read_the_old_one() {
        var reader = new FileSecretProvider(_path);
        new FileSecretProvider(_path).Set("sql:analytics", "before");
        Assert.IsTrue(reader.TryGet("sql:analytics", out var first));
        Assert.AreEqual("before", first);

        new FileSecretProvider(_path).Set("sql:analytics", "after");
        Assert.IsTrue(reader.TryGet("sql:analytics", out var second));
        Assert.AreEqual("after", second);
    }

    /// <summary>The container's umask is 0644, which would leave every password
    /// readable by anything else running in it.</summary>
    [TestMethod]
    public void The_file_is_readable_only_by_its_owner() {
        if (OperatingSystem.IsWindows()) {
            Assert.Inconclusive("Unix file modes only.");
            return;
        }
        new FileSecretProvider(_path).Set("sql:analytics", "hunter2");

        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path),
            "group and world must not be able to read the passwords");
    }

    /// <summary>
    /// Missing and unreadable both mean "no secret", not a crash: this is on the
    /// path of every connection the server opens.
    /// </summary>
    [TestMethod]
    public void An_absent_or_broken_file_resolves_to_nothing() {
        Assert.IsFalse(new FileSecretProvider(_path).TryGet("sql:analytics", out _));

        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, "{ this is not json");
        Assert.IsFalse(new FileSecretProvider(_path).TryGet("sql:analytics", out _));
    }

    /// <summary>
    /// The way out of the file. A container that gains a real credential store must
    /// not keep its passwords in plain text beside the encrypted ones — and must not
    /// lose them either, since nobody can retype what they cannot read.
    /// </summary>
    [TestMethod]
    public void Adopting_moves_every_secret_into_the_real_store_and_removes_the_file() {
        var file = new FileSecretProvider(_path);
        file.Set("sql:analytics", "hunter2");
        file.Set("slack-hook", "xoxb-token");

        var keyring = new InMemorySecretProvider();
        var store = SecretStore.ForProviders(keyring, file, new EnvironmentSecretProvider());

        Assert.AreEqual(2, store.AdoptFileSecrets());
        Assert.IsTrue(keyring.TryGet("sql:analytics", out var moved));
        Assert.AreEqual("hunter2", moved);
        Assert.IsTrue(keyring.TryGet("slack-hook", out _));
        Assert.IsFalse(File.Exists(_path), "the plaintext file is the thing being removed");
        // Still resolvable, which is the point: the connections that named these keep working.
        Assert.IsTrue(store.TryResolve("sql:analytics", out var after));
        Assert.AreEqual("hunter2", after);
    }

    /// <summary>
    /// Nowhere better to put them is not a reason to delete them. A server with no
    /// credential store keeps the file it has, exactly as it was.
    /// </summary>
    [TestMethod]
    public void Adopting_leaves_the_file_alone_when_there_is_nowhere_to_move_it() {
        var file = new FileSecretProvider(_path);
        file.Set("sql:analytics", "hunter2");

        var store = SecretStore.ForProviders(file, new EnvironmentSecretProvider());
        Assert.AreEqual(0, store.AdoptFileSecrets());
        Assert.IsTrue(File.Exists(_path));
        Assert.IsTrue(store.TryResolve("sql:analytics", out _));

        // And an empty file is not deleted either — there is nothing to have moved.
        new FileSecretProvider(_path).Delete("sql:analytics");
        var withKeyring = SecretStore.ForProviders(
            new InMemorySecretProvider(), new FileSecretProvider(_path));
        Assert.AreEqual(0, withKeyring.AdoptFileSecrets());
    }

    /// <summary>
    /// The whole point of the variable: with it set, a machine with no OS
    /// credential store can still be told a password. Without it, nothing changes.
    /// </summary>
    [TestMethod]
    public void The_chain_gains_a_writable_provider_only_when_the_variable_names_a_file() {
        var before = Environment.GetEnvironmentVariable(FileSecretProvider.PathVariable);
        try {
            Environment.SetEnvironmentVariable(FileSecretProvider.PathVariable, null);
            CollectionAssert.DoesNotContain(new SecretStore().ProviderNames.ToArray(), "file");

            Environment.SetEnvironmentVariable(FileSecretProvider.PathVariable, _path);
            var store = new SecretStore();
            CollectionAssert.Contains(store.ProviderNames.ToArray(), "file");
            Assert.IsTrue(store.CanPersist);

            // Not ahead of an OS store where one exists — this machine's Keychain
            // must keep answering first.
            var names = store.ProviderNames.ToList();
            if (names.Contains("keychain") || names.Contains("credential-manager")) {
                Assert.IsTrue(names.IndexOf("file") > 0);
            }
            Assert.IsTrue(names.IndexOf("file") < names.IndexOf("env"),
                "the file is writable; the environment is not, so it comes first");
        } finally {
            Environment.SetEnvironmentVariable(FileSecretProvider.PathVariable, before);
        }
    }
}
