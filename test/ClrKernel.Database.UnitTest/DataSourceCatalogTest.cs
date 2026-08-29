using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Database.UnitTest;

/// <summary>
/// Opening a connections.json node through whichever provider its <c>$type</c>
/// names.
/// <para>
/// The mechanism is a convention — <c>ClrKernel.Database.Provider.X</c> exposes
/// <c>X.FromConfig(name, secrets)</c> — found by reflection at the moment of the
/// question, because the opt-in providers are loaded by <c>#r</c> partway through
/// a session and a registry they write themselves into needs their code to have
/// run first. A convention with no compile-time check is a convention that needs
/// a test.
/// </para>
/// </summary>
[TestClass]
public class DataSourceCatalogTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.SetCurrentDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() {
        Directory.SetCurrentDirectory(Path.GetTempPath());
        try {
            Directory.Delete(_dir, recursive: true);
        } catch (IOException) {
            // A test that cannot tidy up is not a test that failed.
        }
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_dir, "connections.json"), json);

    [TestMethod]
    public void The_providers_this_suite_references_follow_the_convention() {
        // Oracle and Odbc are referenced by this test project, so they are loaded
        // and must be openable. If either ever renames FromConfig or changes its
        // signature, this is where it shows up rather than in a user's notebook.
        Assert.IsTrue(DataSourceCatalog.CanOpen("Oracle"));
        Assert.IsTrue(DataSourceCatalog.CanOpen("Odbc"));
        Assert.IsTrue(DataSourceCatalog.CanOpen("oracle"), "$type matching ignores case");
        CollectionAssert.IsSubsetOf(
            new[] { "Oracle", "Odbc" }, DataSourceCatalog.Available().ToList());
    }

    [TestMethod]
    public void A_provider_that_is_not_loaded_says_which_package_to_load() {
        // The failure that matters: not "connection not found", which sends the
        // reader looking for a typo, but the line to paste.
        Assert.IsFalse(DataSourceCatalog.CanOpen("Snowflake"));
        WriteConfig(@"{ ""dw"": { ""$type"": ""Snowflake"", ""account"": ""x"" } }");

        var refusal = Assert.ThrowsExactly<ConnectionConfigException>(
            () => DataSourceCatalog.Open("Snowflake", "dw"));

        StringAssert.Contains(refusal.Message, "dw");
        StringAssert.Contains(refusal.Message, "Snowflake provider");
        StringAssert.Contains(refusal.Message, "#r \"nuget: ClrKernel.Database.Provider.Snowflake\"");
    }

    [TestMethod]
    public void A_node_with_no_type_is_said_plainly() {
        var refusal = Assert.ThrowsExactly<ConnectionConfigException>(
            () => DataSourceCatalog.Open(null, "dw"));
        StringAssert.Contains(refusal.Message, "$type");
    }

    [TestMethod]
    public void The_provider_s_own_error_is_what_reaches_the_caller() {
        // Reflection must not turn "serviceName is required" into
        // "TargetInvocationException", which tells nobody anything.
        WriteConfig(@"{ ""erp"": { ""$type"": ""Oracle"", ""server"": ""dbhost"" } }");

        var failure = Assert.ThrowsExactly<ConnectionConfigException>(
            () => DataSourceCatalog.Open("Oracle", "erp"));

        StringAssert.Contains(failure.Message, "serviceName");
    }

    [TestMethod]
    public void An_odbc_node_opens_without_touching_a_database() {
        // Building the DataSource is all this proves — no driver is installed here
        // and opening it would need one. That is the seam: the catalog's job ends
        // at handing back something that knows how to connect.
        WriteConfig(@"{ ""warehouse"": { ""$type"": ""Odbc"", ""connectionString"": ""Driver={None};Server=x"" } }");

        var source = DataSourceCatalog.Open("Odbc", "warehouse");

        Assert.IsNotNull(source);
        Assert.AreEqual("warehouse", source.Name);
    }
}
