using System;
using System.Diagnostics;
using System.Linq;
using ClrKernel.Core.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Windows Credential Manager, exercised through the public <see cref="SecretStore"/>
/// the way a connection does.
///
/// <para>
/// It runs on Windows and reports inconclusive elsewhere, which until there is a
/// Windows CI job means it runs nowhere — that is the point of writing it now
/// rather than after. These are the parts of §16 of the Windows checklist a
/// machine can answer; what is left there is what a person has to look at.
/// </para>
/// </summary>
[TestClass]
public class WindowsCredentialTest {
    /// <summary>Distinct per run so a crashed run cannot make the next one pass.</summary>
    private readonly string _ref = "clrkernel-test:" + Guid.NewGuid().ToString("N");

    [TestCleanup]
    public void Cleanup() {
        if (OperatingSystem.IsWindows()) {
            try {
                new SecretStore(cacheLocally: false).Delete(_ref);
            } catch (Exception) {
                // A test that could not store has nothing to remove.
            }
        }
    }

    [TestMethod]
    public void A_password_saved_here_is_read_back_from_the_credential_manager() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Inconclusive("Credential Manager is Windows-only.");
            return;
        }
        // No cache: an in-memory hit would pass this test without the credential
        // store ever being touched, which is the one thing it is about.
        var store = new SecretStore(cacheLocally: false);
        CollectionAssert.Contains(store.ProviderNames.ToArray(), "credential-manager",
            "the OS store must be in the chain, or this is testing the environment");

        store.Store(_ref, "hunter2");

        Assert.IsTrue(new SecretStore(cacheLocally: false).TryResolve(_ref, out var read),
            "a fresh store, so this is the credential manager and not the first one's memory");
        Assert.AreEqual("hunter2", read);
    }

    /// <summary>
    /// A reference with a colon in it — `pg:demo` — makes the target name
    /// `ClrKernel:pg:demo`, which has two. Worth pinning: the provider builds that
    /// name by concatenation, and nothing else says the second colon is fine.
    /// </summary>
    [TestMethod]
    public void A_reference_containing_a_colon_round_trips() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Inconclusive("Credential Manager is Windows-only.");
            return;
        }
        var store = new SecretStore(cacheLocally: false);
        store.Store(_ref, "with:colons:in:it");

        Assert.IsTrue(new SecretStore(cacheLocally: false).TryResolve(_ref, out var read));
        Assert.AreEqual("with:colons:in:it", read);
    }

    /// <summary>
    /// The open question in §16: whether a credential written by <c>cmdkey</c> is
    /// one this can read.
    ///
    /// <para>
    /// The provider writes the blob as UTF-16 and reads it with
    /// <c>PtrToStringUni</c>; if cmdkey encodes differently the value comes back as
    /// mojibake rather than as an error. So the assertion is not "it works" — that
    /// is what nobody knows — but the property that matters either way: a
    /// hand-written credential must never read back as *something else*. Found, and
    /// equal, is a pass; not found is a pass and means the documentation is right to
    /// send people elsewhere. Anything else is the failure worth being told about,
    /// and the test message is what closes the checklist item.
    /// </para>
    /// </summary>
    [TestMethod]
    public void A_credential_written_by_cmdkey_is_never_read_back_as_the_wrong_value() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Inconclusive("cmdkey is Windows-only.");
            return;
        }
        const string password = "hunter2";
        var target = "ClrKernel:" + _ref;
        var written = Run("cmdkey", $"/generic:{target} /user:ClrKernel /pass:{password}");
        if (written != 0) {
            Assert.Inconclusive("cmdkey is not available or refused to write.");
            return;
        }
        try {
            if (!new SecretStore(cacheLocally: false).TryResolve(_ref, out var read)) {
                Assert.Inconclusive(
                    "A cmdkey-written credential is not visible to the provider. That is a "
                    + "supported answer: docs/secrets.md tells Windows users to save from the "
                    + "app or use CLRKERNEL_SECRET_*, and this records why.");
                return;
            }
            Assert.AreEqual(password, read,
                "cmdkey wrote a credential the provider decoded into something else — the "
                + "encodings disagree, and a connection would fail with a wrong password "
                + "rather than a missing one. docs/secrets.md must keep saying not to use it.");
        } finally {
            Run("cmdkey", $"/delete:{target}");
        }
    }

    private static int Run(string file, string arguments) {
        try {
            using var p = Process.Start(new ProcessStartInfo(file, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p.WaitForExit(30_000);
            return p.ExitCode;
        } catch (Exception) {
            return -1;
        }
    }
}
