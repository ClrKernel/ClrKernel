using System;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A value the store hands out is a value the kernel will mask from then on.
/// Whatever provider it came from — this one is the environment, which is the
/// one every headless run and every Studio job has.
/// </summary>
[TestClass]
public class SecretResolutionRedactsTest {
    [TestMethod]
    public void Resolving_a_secret_registers_it_for_redaction() {
        SecretRedaction.Clear();
        const string value = "resolved-secret-4b3a2c";
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_RESOLVE_REDACT_TEST", value);
        try {
            Assert.AreEqual("here: resolved-secret-4b3a2c", SecretRedaction.Redact("here: " + value),
                "nothing is masked before anything has been resolved");

            var store = SecretStore.ForProviders(new EnvironmentSecretProvider());
            Assert.AreEqual(value, store.Resolve("RESOLVE_REDACT_TEST"), "the cell still gets the real value");

            Assert.AreEqual("here: ***", SecretRedaction.Redact("here: " + value),
                "and from now on nothing it prints carries it");
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_RESOLVE_REDACT_TEST", null);
            SecretRedaction.Clear();
        }
    }
}
