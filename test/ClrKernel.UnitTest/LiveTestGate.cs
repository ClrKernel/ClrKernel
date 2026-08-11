using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Gate for tests that need a real backend.
/// <para>
/// By default a missing backend is <see cref="Assert.Inconclusive(string)"/> — that keeps CI green
/// on machines with no server. The hazard is the other case: someone sitting down to *verify* a
/// release runs the filtered suite, forgets the environment variable, sees "Passed!", and ticks the
/// box having executed nothing. That is exactly how the original P4b gate came to be satisfiable
/// without running a line of the code it was meant to cover (HANDOFF-17 §5).
/// </para>
/// <para>
/// So set <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> when you intend to verify: a missing backend then
/// <b>fails</b> instead of skipping, and a silent no-op becomes impossible.
/// </para>
/// </summary>
internal static class LiveTestGate {
    private const string RequireVar = "CLRKERNEL_TEST_REQUIRE_LIVE";

    /// <summary>True when the caller has declared this run is a verification run.</summary>
    internal static bool LiveRunRequired =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RequireVar));

    /// <summary>
    /// Skips (or, on a verification run, fails) when <paramref name="value"/> is not configured.
    /// </summary>
    /// <param name="value">The environment variable's value.</param>
    /// <param name="variable">Its name, for the message.</param>
    /// <param name="what">What the tests cover, for the message.</param>
    internal static void Require(string value, string variable, string what) {
        if (!string.IsNullOrWhiteSpace(value)) {
            return;
        }

        var message = $"Set {variable} to run {what}.";
        if (LiveRunRequired) {
            Assert.Fail($"{message} {RequireVar} is set, so this run was expected to reach a real " +
                        "backend — skipping would report success without verifying anything.");
        }
        Assert.Inconclusive(message);
    }
}
