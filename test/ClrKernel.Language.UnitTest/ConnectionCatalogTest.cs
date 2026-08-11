using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The connection-catalog seam: the editor's connection UI reached through
/// <c>ICellLanguage.Connections</c> rather than through per-language RPCs in the host.
/// <para>
/// These go through the same path the JSON-RPC host uses, which is the point — the host now
/// references no <c>Language.*</c> package, so nothing else would catch a language forgetting to
/// expose its catalog, or exposing one that doesn't work.
/// </para>
/// </summary>
[TestClass]
public class ConnectionCatalogTest {
    private static InteractiveScriptEngine NewEngine() =>
        new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);

    [TestMethod]
    public void Only_the_languages_that_connect_to_something_have_a_catalog() {
        var engine = NewEngine();
        Assert.IsNotNull(engine.Languages.ById("sql")?.Connections);
        Assert.IsNotNull(engine.Languages.ById("dax")?.Connections);
        // Nothing to connect to — the host reports "no connection support" rather than failing.
        Assert.IsNull(engine.Languages.ById("http")?.Connections);
        Assert.IsNull(engine.Languages.ById("mermaid")?.Connections);
        Assert.IsNull(engine.Languages.ById("powershell")?.Connections);
    }

    [TestMethod]
    public async Task Sql_catalog_lists_what_a_connect_directive_registered() {
        var engine = NewEngine();
        await engine.ExecuteAsync("#!sql-connect --name warehouse --server dw --database reports --default");

        var catalog = engine.Languages.ById("sql").Connections;
        var one = catalog.List().Single();

        Assert.AreEqual("warehouse", one.Name);
        Assert.AreEqual("dw", one.Server);
        Assert.AreEqual("reports", one.Database);
        Assert.IsTrue(one.IsDefault);
        Assert.AreEqual("warehouse", catalog.DefaultName);
    }

    [TestMethod]
    public void Sql_catalog_adds_removes_and_sets_a_default() {
        var catalog = NewEngine().Languages.ById("sql").Connections;

        catalog.Add("#!sql-connect --name a --server s1 --database d");
        catalog.Add("#!sql-connect --name b --server s2 --database d");
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, catalog.List().Select(c => c.Name).ToArray());

        catalog.SetDefault("b");
        Assert.AreEqual("b", catalog.DefaultName);

        Assert.IsTrue(catalog.Remove("a"));
        Assert.IsFalse(catalog.Remove("a"), "removing twice should report nothing was there");
        Assert.AreEqual("b", catalog.List().Single().Name);
    }

    [TestMethod]
    public void Dax_catalog_has_the_same_four_operations() {
        // DAX had only list/add RPCs before; remove and setDefault existed on the registry all
        // along and were simply never exposed. Routing through the catalog gives it both.
        var catalog = NewEngine().Languages.ById("dax").Connections;

        catalog.Add("#!dax-connect --name sales --server ssas --database Sales");
        catalog.Add("#!dax-connect --name finance --server ssas --database Finance");
        catalog.SetDefault("finance");
        Assert.AreEqual("finance", catalog.DefaultName);

        Assert.IsTrue(catalog.Remove("sales"));
        Assert.AreEqual("finance", catalog.List().Single().Name);
    }

    [TestMethod]
    public void Config_file_support_is_a_capability_the_host_type_checks_for() {
        var engine = NewEngine();
        // SQL keeps connections in connections.json; DAX has no such concept yet. The host asks
        // the type rather than reading a flag, so DAX simply isn't offered the config methods.
        Assert.IsInstanceOfType(engine.Languages.ById("sql").Connections, typeof(IConfigBackedConnections));
        Assert.IsNotInstanceOfType(engine.Languages.ById("dax").Connections, typeof(IConfigBackedConnections));
    }

    [TestMethod]
    public void Config_status_reports_no_file_without_inventing_one() {
        var config = (IConfigBackedConnections)NewEngine().Languages.ById("sql").Connections;
        var empty = Path.Combine(Path.GetTempPath(), "clrkernel-no-config-" + Path.GetRandomFileName());
        Directory.CreateDirectory(empty);
        try {
            var status = config.Status(empty);
            Assert.IsFalse(status.Found);
            Assert.IsNull(status.Path);
            Assert.AreEqual(0, status.Names.Count);
            Assert.AreEqual(0, config.LoadFromConfig(empty).Count);
        } finally {
            Directory.Delete(empty, recursive: true);
        }
    }
}
