using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using Azure.Core;
using Azure.Identity;
using ClrKernel.Database.Entra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The live half of the P6 gate: proof that a token can actually be acquired through <b>both</b>
/// credential chains after they moved into <c>ClrKernel.Database.Entra</c>.
/// <para>
/// Read-only by construction — acquiring a token authenticates, it does not touch a subscription,
/// create anything, or write anywhere. Everything here works off an <c>az login</c> alone; no
/// Fabric, Analysis Services or SQL resource is needed. Connecting to an actual semantic model is
/// checklist §11a and is not covered here.
/// </para>
/// <para>
/// Opt in with <c>CLRKERNEL_TEST_ENTRA=1</c> (plus <c>az login</c>). Skipped otherwise, or failed
/// if <c>CLRKERNEL_TEST_REQUIRE_LIVE</c> is also set.
/// </para>
/// </summary>
[TestClass]
public class EntraLiveTest {
    /// <summary>Caps every token call. A credential that decides to prompt cannot hang the run.</summary>
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; }

    private static string OptIn => Environment.GetEnvironmentVariable("CLRKERNEL_TEST_ENTRA");

    /// <summary>Overridable in case a tenant won't issue for the default resource.</summary>
    private static string Scope =>
        Environment.GetEnvironmentVariable("CLRKERNEL_TEST_ENTRA_SCOPE") ?? EntraScopes.SqlDatabase;

    [TestInitialize]
    public void RequireTenant() {
        LiveTestGate.Require(OptIn, "CLRKERNEL_TEST_ENTRA", "the Entra sign-in tests (run az login first)");
        RequireSilentCredential();
    }

    /// <summary>
    /// Requires a token to be obtainable <b>silently</b> before running anything.
    /// <para>
    /// Both chains under test end in an interactive credential — that is the point of them — so
    /// without this a run with no valid <c>az login</c> pops a browser window and blocks until a
    /// human deals with it. Unacceptable in CI, and worse on a developer machine where the opt-in
    /// was left set from an earlier session.
    /// </para>
    /// <para>
    /// Checking <see cref="AzureCliCredential"/> against the same scope the tests use also makes
    /// the tests themselves non-interactive: <c>DefaultAzureCredential</c> tries the CLI before its
    /// interactive fallback, so once the CLI is known to work for this scope, neither chain can
    /// reach its browser link.
    /// </para>
    /// </summary>
    private static void RequireSilentCredential() {
        try {
            using var cts = new CancellationTokenSource(_timeout);
            new AzureCliCredential().GetToken(new TokenRequestContext(new[] { Scope }), cts.Token);
        } catch (Exception e) {
            var message =
                $"No silent Entra token for '{Scope}' ({e.GetType().Name}). Run 'az login' first. " +
                "Skipping rather than letting the credential chain open a browser and wait.";
            if (LiveTestGate.LiveRunRequired) {
                Assert.Fail(message + " CLRKERNEL_TEST_REQUIRE_LIVE is set, so this was meant to reach a tenant.");
            }
            Assert.Inconclusive(message);
        }
    }

    [TestMethod]
    public void Analysis_Services_chain_acquires_a_token() =>
        AcquireAndReport("Analysis Services", EntraAuth.DefaultWithInteractiveFallback());

    [TestMethod]
    public void Fabric_chain_acquires_a_token() =>
        AcquireAndReport("Fabric", EntraAuth.DefaultThenInteractiveBrowser());

    [TestMethod]
    public void Both_chains_resolve_the_same_identity_on_this_machine() {
        // Both should land on AzureCliCredential here, so a mismatch means one chain is probing
        // somewhere the other isn't — the P6 risk, and the thing that never surfaces as an error.
        var ssas = Identify(Token(EntraAuth.DefaultWithInteractiveFallback()).Token);
        var fabric = Identify(Token(EntraAuth.DefaultThenInteractiveBrowser()).Token);

        TestContext.WriteLine($"Analysis Services chain → {ssas}");
        TestContext.WriteLine($"Fabric chain            → {fabric}");
        Assert.AreEqual(ssas, fabric,
            "the two chains resolved different identities; confirm which one is the account you expect");
    }

    private static AccessToken Token(TokenCredential credential) {
        using var cts = new CancellationTokenSource(_timeout);
        return EntraAuth.Token(credential, Scope, cts.Token);
    }

    private void AcquireAndReport(string label, TokenCredential credential) {
        AccessToken token;
        try {
            token = Token(credential);
        } catch (Exception e) {
            throw new AssertFailedException(
                $"The {label} chain could not get a token for '{Scope}'. If this is a tenant/resource " +
                $"problem rather than a ClrKernel one, set CLRKERNEL_TEST_ENTRA_SCOPE to a resource this " +
                $"account can reach. Underlying error: {e.Message}", e);
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(token.Token), $"{label}: empty token");
        Assert.IsTrue(token.ExpiresOn > DateTimeOffset.UtcNow, $"{label}: token already expired");
        // The identity, never the token itself.
        TestContext.WriteLine($"{label} chain → {Identify(token.Token)}, expires {token.ExpiresOn:u}");
    }

    /// <summary>
    /// The signed-in identity from the token's payload claims. Reads only who/where, never the
    /// signature or any secret material, and the token is not logged.
    /// </summary>
    private static string Identify(string jwt) {
        var parts = jwt.Split('.');
        if (parts.Length < 2) {
            return "(not a JWT)";
        }
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var root = doc.RootElement;
        string Claim(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

        var who = Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name")
                  ?? Claim("appid") ?? Claim("oid") ?? "(unknown principal)";
        var tenant = Claim("tid");
        return tenant is null ? who : $"{who} (tenant {tenant})";
    }
}
