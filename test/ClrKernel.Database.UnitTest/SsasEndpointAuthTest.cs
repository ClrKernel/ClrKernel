using System;
using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Language.Dax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A cloud endpoint opened with Integrated auth can never work, and used to fail with ADOMD's
/// "Authentication failed for all authenticators" — the same message as a token that was sent and
/// rejected. The connection strings are byte-identical, so nothing distinguished them.
/// <para>
/// This is reachable from the cube button: pasting a <c>powerbi://</c> URL into the on-prem
/// "Server / host" prompt produces exactly this spec. These tests run offline — the guard fires
/// before any connection is attempted.
/// </para>
/// </summary>
[TestClass]
public class SsasEndpointAuthTest {
    private static SsasConnectionSpec Spec(string directive) => DaxDirectives.ParseConnect(directive).Spec;

    [TestMethod]
    public void A_powerbi_url_entered_as_a_plain_server_is_refused_with_the_fix() {
        var spec = Spec("#!dax-connect --name p --server \"powerbi://api.powerbi.com/v1.0/myorg/WS\" --database M");
        Assert.AreEqual(SsasAuthMode.Integrated, spec.Auth, "this is the shape the on-prem prompt produces");

        var e = Assert.ThrowsExactly<InvalidOperationException>(() => new SsasConnection(spec).Query("EVALUATE {1}"));
        StringAssert.Contains(e.Message, "Microsoft Entra endpoint");
        StringAssert.Contains(e.Message, "--fabric");
    }

    [TestMethod]
    public void An_asazure_url_entered_as_a_plain_server_points_at_the_azure_as_flag() {
        var spec = Spec("#!dax-connect --name a --server \"asazure://westus.asazure.windows.net/s\" --database M");
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => new SsasConnection(spec).Query("EVALUATE {1}"));
        StringAssert.Contains(e.Message, "--azure-as");
    }

    [TestMethod]
    public void The_directives_that_do_attach_a_token_are_left_alone() {
        // Guarded on auth mode, not on the URL, so the correct Fabric/Azure AS specs pass through
        // to a real connection attempt.
        foreach (var directive in new[] {
            "#!dax-connect --name f --fabric --workspace WS --model M",
            "#!dax-connect --name a --server \"asazure://westus.asazure.windows.net/s\" --database M --azure-as",
        }) {
            var spec = Spec(directive);
            Assert.AreEqual(SsasAuthMode.AzureAd, spec.Auth, directive);
            Assert.IsNotNull(spec.TokenProvider, directive);
        }
    }

    [TestMethod]
    public void An_on_premises_server_still_uses_integrated_auth() {
        var spec = Spec("#!dax-connect --name o --server ssas01 --database M");
        Assert.AreEqual(SsasAuthMode.Integrated, spec.Auth);
        Assert.IsNull(spec.TokenProvider);
        // No guard: a hostname is exactly what Integrated auth is for. It will fail to reach a
        // server here, but with a network error rather than our message.
        try {
            new SsasConnection(spec).Query("EVALUATE {1}");
        } catch (Exception e) {
            Assert.IsFalse(e.Message.Contains("Microsoft Entra endpoint"), "should not be refused by the guard");
        }
    }
}
