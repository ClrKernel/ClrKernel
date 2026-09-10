using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A secret the kernel knows is masked in everything a cell sends out: console
/// lines, display values, and the message of an exception the cell threw.
///
/// <para>
/// The registry is process-wide, so each test clears it and uses values no other
/// test would print. The values are long enough to register — a short one is
/// skipped on purpose, and the first test says why.
/// </para>
/// </summary>
[TestClass]
public class SecretRedactionTest {
    [TestInitialize]
    public void Fresh() => SecretRedaction.Clear();

    [TestCleanup]
    public void Forget() => SecretRedaction.Clear();

    [TestMethod]
    public void Masks_registered_values_and_nothing_else() {
        SecretRedaction.Register("hunter2-hunter2");
        Assert.AreEqual("token=*** ok", SecretRedaction.Redact("token=hunter2-hunter2 ok"));
        Assert.AreEqual("nothing here", SecretRedaction.Redact("nothing here"), "untouched when nothing matches");
        Assert.IsNull(SecretRedaction.Redact(null));

        // A short value is not a secret this can mask: "pass" would mask "password",
        // "compass" and every "pass" in prose, which hides more than it protects.
        SecretRedaction.Register("pass");
        Assert.AreEqual("the password field", SecretRedaction.Redact("the password field"));
    }

    [TestMethod]
    public void A_secret_that_contains_another_is_masked_whole() {
        SecretRedaction.Register("prefix-part");
        SecretRedaction.Register("prefix-part-and-more");
        // Longest first: masking the shorter one inside the longer would leave
        // "***-and-more", which is most of the secret.
        Assert.AreEqual("got ***.", SecretRedaction.Redact("got prefix-part-and-more."));
    }

    [TestMethod]
    public void Seeding_takes_every_variable_under_the_prefix() {
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SEED_TEST_A", "seed-value-alpha");
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SEED_TEST_B", "seed-value-beta");
        try {
            Assert.IsTrue(SecretRedaction.SeedFromEnvironment() >= 2);
            Assert.AreEqual("*** and ***", SecretRedaction.Redact("seed-value-alpha and seed-value-beta"));
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SEED_TEST_A", null);
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SEED_TEST_B", null);
        }
    }

    /// <summary>
    /// The three ways a cell can let a value out, through the headless runner,
    /// which writes exactly what a scheduled job's artifact holds. The cell reads
    /// the variable directly rather than through the store — the seed at engine
    /// start is what has to catch it.
    /// </summary>
    [TestMethod]
    public async Task A_headless_run_masks_a_printed_displayed_and_thrown_secret() {
        const string secret = "artifact-secret-9f8e7d";
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ARTIFACT_TEST", secret);
        var dir = Path.Combine(Path.GetTempPath(), "clrkernel-redact-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var input = Path.Combine(dir, "nb.nb.md");
            var output = Path.Combine(dir, "out.ipynb");
            File.WriteAllText(input,
                "```csharp\nvar s = Environment.GetEnvironmentVariable(\"CLRKERNEL_SECRET_ARTIFACT_TEST\");\n"
                + "Console.WriteLine(\"printed: \" + s);\n```\n"
                + "```csharp\n\"displayed: \" + Environment.GetEnvironmentVariable(\"CLRKERNEL_SECRET_ARTIFACT_TEST\")\n```\n"
                + "```csharp\nthrow new Exception(\"thrown: \" + Environment.GetEnvironmentVariable(\"CLRKERNEL_SECRET_ARTIFACT_TEST\"));\n```\n");

            InteractiveScriptEngine.RefsFilePath = null;
            var code = await NotebookRunner.RunAsync(
                RunnerOptions.Parse(new[] { input, "-o", output }), NullLoggerFactory.Instance);
            Assert.AreNotEqual(0, code, "the third cell throws");

            var json = File.ReadAllText(output);
            Assert.IsFalse(json.Contains(secret), "the secret is in the artifact:\n" + json);
            var outputs = JsonNode.Parse(json)["cells"].AsArray()
                .SelectMany(c => c["outputs"]?.AsArray() ?? new JsonArray())
                .Select(o => o.ToJsonString()).ToList();
            Assert.IsTrue(outputs.Any(o => o.Contains("printed: ***")), "console line not masked:\n" + json);
            Assert.IsTrue(outputs.Any(o => o.Contains("displayed: ***")), "display value not masked:\n" + json);
            Assert.IsTrue(outputs.Any(o => o.Contains("thrown: ***")), "exception message not masked:\n" + json);
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ARTIFACT_TEST", null);
        }
    }
}
