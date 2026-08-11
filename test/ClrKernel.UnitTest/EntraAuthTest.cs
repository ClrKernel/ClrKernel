using Azure.Core;
using Azure.Identity;
using ClrKernel.Database.Entra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Guards the one thing about P6 that no live run would catch either: that the two providers'
/// credential chains stayed <b>different</b>. They look like duplication, and merging them would
/// change which identity a developer signs in as — a working connection under the wrong account,
/// never a compile error. Constructing a credential contacts nothing, so this runs offline.
/// </summary>
[TestClass]
public class EntraAuthTest {
    [TestMethod]
    public void The_two_credential_chains_are_not_the_same() {
        var ssas = EntraAuth.DefaultWithInteractiveFallback();
        var fabric = EntraAuth.DefaultThenInteractiveBrowser();

        // Analysis Services: DefaultAzureCredential with interactive enabled.
        Assert.IsInstanceOfType(ssas, typeof(DefaultAzureCredential));
        // Fabric: an explicit chain, non-interactive default first, browser second.
        Assert.IsInstanceOfType(fabric, typeof(ChainedTokenCredential));

        Assert.AreNotEqual(ssas.GetType(), fabric.GetType(),
            "these were deliberately left distinct in P6 — see the remarks on EntraAuth");
    }

    [TestMethod]
    public void ClientSecret_rejects_each_blank_argument_by_name() {
        AssertBlankRejected("tenantId", () => EntraAuth.ClientSecret(" ", "c", "s"));
        AssertBlankRejected("clientId", () => EntraAuth.ClientSecret("t", " ", "s"));
        AssertBlankRejected("clientSecret", () => EntraAuth.ClientSecret("t", "c", " "));
    }

    [TestMethod]
    public void Token_rejects_a_missing_credential_or_scope() {
        Assert.ThrowsExactly<System.ArgumentNullException>(
            () => EntraAuth.Token(null, EntraScopes.PowerBi));
        Assert.ThrowsExactly<System.ArgumentException>(
            () => EntraAuth.Token(new DefaultAzureCredential(), " "));
    }

    private static void AssertBlankRejected(string parameter, System.Action act) {
        var e = Assert.ThrowsExactly<System.ArgumentException>(act);
        Assert.AreEqual(parameter, e.ParamName);
    }
}
