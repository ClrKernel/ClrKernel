using System.Collections.Generic;
using System.Linq;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlStepDirectiveTest {
    [TestMethod]
    public void ParseCell_extracts_step_and_needs() {
        var req = SqlDirectives.ParseCell("-- step load_facts\n-- needs extract_orders, load_dim\nSELECT 1");
        Assert.AreEqual("load_facts", req.StepName);
        CollectionAssert.AreEqual(new[] { "extract_orders", "load_dim" }, req.Needs.ToArray());
    }

    [TestMethod]
    public void ParseCell_step_with_connection() {
        var req = SqlDirectives.ParseCell("-- connections warehouse\n-- step build\nSELECT 1");
        Assert.AreEqual("build", req.StepName);
        Assert.AreEqual("warehouse", req.ConnectionName);
    }

    [TestMethod]
    public void ParseRun_and_ParseDeploy() {
        var run = SqlOrchestrationDirectives.ParseRun("#!sql-run --select facts --max-parallel 8");
        CollectionAssert.AreEqual(new[] { "facts" }, run.Select.ToArray());
        Assert.AreEqual(8, run.MaxParallel);

        var dep = SqlOrchestrationDirectives.ParseDeploy("#!sql-deploy --connection wh --path ./sql --recurse --dry-run");
        Assert.AreEqual("wh", dep.Connection);
        Assert.AreEqual("./sql", dep.Options.Path);
        Assert.IsTrue(dep.Options.Recurse);
        Assert.IsTrue(dep.Options.DryRun);
    }
}

[TestClass]
public class SqlContextCompletionTest {
    private static SqlCompletionContext Ctx() => new SqlCompletionContext {
        ConnectionNames = new[] { "analytics", "warehouse" },
        StepNames = new[] { "alpha", "beta", "extract" },
    };

    private static List<string> Labels(string code) {
        var c = SqlLanguage.Complete(code, code.Length, Ctx());
        return c.Items.Select(i => i.Label).ToList();
    }

    [TestMethod]
    public void Completes_magic_names() {
        var labels = Labels("#!sql-");
        CollectionAssert.Contains(labels, "#!sql-merge");
        CollectionAssert.Contains(labels, "#!sql-deploy");
        CollectionAssert.DoesNotContain(labels, "#!sql");
    }

    [TestMethod]
    public void Completes_magic_flags() {
        var labels = Labels("#!sql-bulk --f");
        CollectionAssert.Contains(labels, "--from");
        CollectionAssert.Contains(labels, "--from-table");
    }

    [TestMethod]
    public void Completes_connection_names_after_connection_flag() {
        var labels = Labels("#!sql-bulk --from ");
        CollectionAssert.Contains(labels, "analytics");
        CollectionAssert.Contains(labels, "warehouse");
    }

    [TestMethod]
    public void Completes_auth_values() {
        var labels = Labels("#!sql-connect --name x --auth ");
        CollectionAssert.Contains(labels, "integrated");
        CollectionAssert.Contains(labels, "entra-password");
    }

    [TestMethod]
    public void Completes_directive_keywords() {
        CollectionAssert.Contains(Labels("-- ne"), "needs");
        CollectionAssert.Contains(Labels("-- "), "step");
    }

    [TestMethod]
    public void Completes_step_names_after_needs() {
        var labels = Labels("-- needs a");
        CollectionAssert.Contains(labels, "alpha");
        CollectionAssert.DoesNotContain(labels, "beta");
    }

    [TestMethod]
    public void Completes_connections_in_directive() {
        var labels = Labels("-- connections ");
        CollectionAssert.Contains(labels, "analytics");
    }

    [TestMethod]
    public void Falls_back_to_tsql_keywords_in_statements() {
        var labels = Labels("SEL");
        CollectionAssert.Contains(labels, "SELECT");
    }
}
