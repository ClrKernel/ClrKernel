using System;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Language.Dax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A cube's password now comes from the same place a SQL connection's does — the
/// <see cref="SecretStore"/>, which reads the OS credential manager first and
/// <c>CLRKERNEL_SECRET_*</c> after. DAX used to read environment variables only, so a password
/// saved into Keychain / Credential Manager was invisible to it.
/// <para>
/// Nothing here writes to the credential store: <c>SecretStore.Store</c> targets the real OS
/// keychain, which a unit test has no business touching. The env path is exercised instead, which
/// is the same code path through the same store.
/// </para>
/// </summary>
[TestClass]
public class DaxSecretStoreTest {
    private const string _directive =
        "#!dax-connect --name cube --server ssas01 --database M --user svc --secret ssas:app";

    [TestMethod]
    public void A_password_resolves_through_the_shared_secret_store() {
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SSAS_APP", "hunter2");
        try {
            var session = new SsasSession();
            session.Connect(_directive);
            var spec = session.Cubes.Resolve("cube");
            Assert.AreEqual(SsasAuthMode.UserPassword, spec.Auth);
            Assert.AreEqual("hunter2", spec.Password);
            Assert.AreEqual("ssas:app", spec.SecretRef, "the reference is kept so the cube can be saved");
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SSAS_APP", null);
        }
    }

    [TestMethod]
    public void The_reference_itself_still_works_as_an_environment_variable_name() {
        // The store's env provider tries the reference verbatim before the prefixed form, which is
        // what the old hand-rolled DAX resolver did. Existing notebooks keep working.
        Environment.SetEnvironmentVariable("SSAS_PLAIN_REF", "from-plain");
        try {
            var session = new SsasSession();
            session.Connect("#!dax-connect --name c --server s --database M --user u --secret SSAS_PLAIN_REF");
            Assert.AreEqual("from-plain", session.Cubes.Resolve("c").Password);
        } finally {
            Environment.SetEnvironmentVariable("SSAS_PLAIN_REF", null);
        }
    }

    [TestMethod]
    public void A_missing_secret_names_every_place_it_looked() {
        var e = Assert.ThrowsExactly<SecretNotFoundException>(() => new SsasSession().Connect(_directive));
        // The store's message lists its providers, so "not in Keychain either" is visible. The old
        // DAX error only ever mentioned an environment variable.
        StringAssert.Contains(e.Message, "ssas:app");
        StringAssert.Contains(e.Message, "Looked in");
    }

    [TestMethod]
    public void The_secret_reference_can_be_read_off_a_directive_before_parsing_it() {
        // Lets the connection UI put a typed password in the store under the right key before the
        // directive is registered, which is when the reference gets resolved.
        Assert.AreEqual("ssas:app", DaxDirectives.SecretRefOf(_directive));
        Assert.IsNull(DaxDirectives.SecretRefOf("#!dax-connect --name c --fabric --workspace W --model M"));
    }

    [TestMethod]
    public void A_cube_with_no_password_is_unaffected() {
        var session = new SsasSession();
        session.Connect("#!dax-connect --name f --fabric --workspace W --model M");
        Assert.AreEqual(SsasAuthMode.AzureAd, session.Cubes.Resolve("f").Auth);
        Assert.IsNull(session.Cubes.Resolve("f").SecretRef);
    }
}
