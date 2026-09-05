using System;
using System.IO;
using System.Linq;
using ClrKernel.Core.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Which store a server keeps its passwords in is a choice, not a discovery. The
/// point of each case here is that asking for one and silently getting another is
/// the failure worth preventing.
/// </summary>
[TestClass]
public class SecretStoreFactoryTest {
    private string _root;

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-secretstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() {
        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // A temp directory that will not go is not a test failure.
        }
    }

    private JobsOptions Options(string kind, string file = null) =>
        new() { DataDir = _root, SecretStore = kind, SecretsFile = file };

    [TestMethod]
    public void A_file_store_writes_the_file_it_was_told_to() {
        var path = Path.Combine(_root, "nested", "secrets.json");
        var notes = new System.Collections.Generic.List<string>();

        var store = SecretStoreFactory.Create(Options("file", path), notes.Add);
        store.Store("api-key", "sk-value");

        Assert.IsTrue(File.Exists(path), "and created the folder for it");
        StringAssert.Contains(File.ReadAllText(path), "sk-value");
        Assert.AreEqual("sk-value", store.Resolve("api-key"));
        Assert.IsTrue(store.CanPersist);
        // Said out loud, because an unencrypted file is a thing to know about.
        StringAssert.Contains(string.Join(" ", notes), "unencrypted");
    }

    [TestMethod]
    public void A_file_store_defaults_to_the_data_directory() {
        var store = SecretStoreFactory.Create(Options("file"), null);
        store.Store("k", "v");

        Assert.IsTrue(File.Exists(SecretStoreFactory.DefaultFile(Options("file"))),
            "beside the run history, never inside a worktree");
        Assert.AreEqual("v", store.Resolve("k"));
    }

    /// <summary>
    /// `os` never falls back to a file. A server told to use the machine's store and
    /// silently given a plaintext file instead is the outcome this refuses.
    /// </summary>
    [TestMethod]
    public void An_os_store_does_not_fall_back_to_a_file() {
        var path = Path.Combine(_root, "secrets.json");
        File.WriteAllText(path, "{}");
        Environment.SetEnvironmentVariable(FileSecretProvider.PathVariable, path);
        try {
            var store = SecretStoreFactory.Create(Options("os"), null);
            CollectionAssert.DoesNotContain(store.ProviderNames.ToArray(), "file");
        } finally {
            Environment.SetEnvironmentVariable(FileSecretProvider.PathVariable, null);
        }
    }

    [TestMethod]
    public void Auto_is_what_every_version_before_this_did() {
        var store = SecretStoreFactory.Create(Options("auto"), null);

        CollectionAssert.Contains(store.ProviderNames.ToArray(), "memory");
        CollectionAssert.Contains(store.ProviderNames.ToArray(), "env");
    }

    [TestMethod]
    public void A_setting_nobody_can_honour_is_refused_at_startup() {
        var e = Assert.ThrowsExactly<ArgumentException>(
            () => SecretStoreFactory.Create(Options("vault"), null));

        StringAssert.Contains(e.Message, "vault");
        StringAssert.Contains(e.Message, "file", "and names what it will accept");
    }
}
