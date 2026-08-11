using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class PipelineGraphTest {
    private static PipelineStep Step(string name, params string[] needs) =>
        new PipelineStep(name, "SELECT 1", "conn", needs);

    [TestMethod]
    public void Topological_order_respects_dependencies() {
        var p = new Pipeline();
        p.Register(Step("c", "a", "b"));
        p.Register(Step("a"));
        p.Register(Step("b", "a"));
        var order = p.TopologicalOrder(p.All.ToList()).Select(s => s.Name).ToList();
        Assert.IsTrue(order.IndexOf("a") < order.IndexOf("b"));
        Assert.IsTrue(order.IndexOf("b") < order.IndexOf("c"));
        Assert.IsTrue(order.IndexOf("a") < order.IndexOf("c"));
    }

    [TestMethod]
    public void Cycle_is_detected() {
        var p = new Pipeline();
        p.Register(Step("a", "b"));
        p.Register(Step("b", "a"));
        Assert.ThrowsExactly<PipelineGraphException>(() => p.Validate());
    }

    [TestMethod]
    public void Missing_dependency_is_reported() {
        var p = new Pipeline();
        p.Register(Step("a", "ghost"));
        Assert.ThrowsExactly<PipelineGraphException>(() => p.Validate());
    }

    [TestMethod]
    public void Select_includes_transitive_upstream() {
        var p = new Pipeline();
        p.Register(Step("extract"));
        p.Register(Step("dim", "extract"));
        p.Register(Step("fact", "dim"));
        p.Register(Step("unrelated"));
        var names = p.Select(new[] { "fact" }).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(new[] { "extract", "dim", "fact" }, names.ToArray());
    }
}

[TestClass]
public class PipelineRunnerTest {
    private static PipelineStep Step(string name, params string[] needs) =>
        new PipelineStep(name, "SELECT 1", "conn", needs);

    [TestMethod]
    public async Task Independent_steps_run_in_parallel() {
        var both = new CountdownEvent(2);
        StepOutcome Exec(PipelineStep s) {
            if (s.Name == "a" || s.Name == "b") {
                both.Signal();
                Assert.IsTrue(both.Wait(3000), "independent steps should run concurrently");
            }
            return StepOutcome.Ok("ok", 0);
        }
        var steps = new[] { Step("a"), Step("b"), Step("c", "a", "b") };
        var result = await new PipelineRunner(4).RunAsync(steps, Exec);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, both.CurrentCount);
    }

    [TestMethod]
    public async Task Failure_skips_dependents_but_not_independent_branches() {
        StepOutcome Exec(PipelineStep s) =>
            s.Name == "a" ? StepOutcome.Fail("boom", 0) : StepOutcome.Ok("ok", 0);
        var steps = new[] { Step("a"), Step("b", "a"), Step("c") };
        var result = await new PipelineRunner(4).RunAsync(steps, Exec);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(StepState.Failed, result.Steps.First(s => s.Step.Name == "a").State);
        Assert.AreEqual(StepState.Skipped, result.Steps.First(s => s.Step.Name == "b").State);
        Assert.AreEqual(StepState.Done, result.Steps.First(s => s.Step.Name == "c").State);
    }

    [TestMethod]
    public async Task Dependent_runs_after_its_dependency() {
        var order = new ConcurrentQueue<string>();
        StepOutcome Exec(PipelineStep s) {
            order.Enqueue(s.Name);
            Thread.Sleep(10);
            return StepOutcome.Ok("ok", 0);
        }
        var steps = new[] { Step("first"), Step("second", "first") };
        await new PipelineRunner(4).RunAsync(steps, Exec);
        var list = order.ToList();
        Assert.IsTrue(list.IndexOf("first") < list.IndexOf("second"));
    }
}

[TestClass]
public class GoBatchSplitterTest {
    [TestMethod]
    public void Splits_on_go_lines() {
        var batches = GoBatchSplitter.Split("SELECT 1\nGO\nSELECT 2\ngo\nSELECT 3");
        CollectionAssert.AreEqual(new[] { "SELECT 1", "SELECT 2", "SELECT 3" }, batches.ToArray());
    }

    [TestMethod]
    public void Go_with_count_and_inline_go_string() {
        var batches = GoBatchSplitter.Split("SELECT 1\nGO 3\nSELECT 'GO'");
        Assert.AreEqual(2, batches.Count);
        StringAssert.Contains(batches[1], "'GO'");
    }
}

[TestClass]
public class CreateOrAlterTest {
    [TestMethod]
    public void Rewrites_create_procedure() {
        Assert.AreEqual("CREATE OR ALTER PROCEDURE dbo.P AS SELECT 1",
            CreateOrAlter.Transform("CREATE PROCEDURE dbo.P AS SELECT 1"));
    }

    [TestMethod]
    public void Leaves_existing_or_alter_and_tables_alone() {
        Assert.AreEqual("CREATE OR ALTER VIEW dbo.V AS SELECT 1",
            CreateOrAlter.Transform("CREATE OR ALTER VIEW dbo.V AS SELECT 1"));
        Assert.AreEqual("CREATE TABLE dbo.T (Id INT)",
            CreateOrAlter.Transform("CREATE TABLE dbo.T (Id INT)"));
    }

    [TestMethod]
    public void Preserves_case_of_object_kind() {
        StringAssert.Contains(CreateOrAlter.Transform("create view dbo.V as select 1"), "CREATE OR ALTER view");
    }
}

[TestClass]
public class DeployRunnerTest {
    [TestMethod]
    public void Plan_reads_files_in_name_order_and_transforms() {
        var dir = Path.Combine(Path.GetTempPath(), "clrdeploy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "02_view.sql"), "CREATE VIEW dbo.V AS SELECT 1");
            File.WriteAllText(Path.Combine(dir, "01_proc.sql"), "CREATE PROCEDURE dbo.P AS SELECT 1\nGO\nSELECT 2");
            var files = DeployRunner.Plan(new DeployOptions { Path = dir });
            Assert.AreEqual(2, files.Count);
            Assert.AreEqual("01_proc.sql", files[0].Name);
            Assert.AreEqual(2, files[0].Batches.Count);
            StringAssert.Contains(files[0].Batches[0], "CREATE OR ALTER PROCEDURE");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Multi_pass_resolves_cross_file_dependencies() {
        // 01_a references B (fails until B exists); 02_b creates B.
        var files = new List<DeployFile> {
            new DeployFile("a", "01_a.sql", new[] { "OBJ:A NEEDS:B" }),
            new DeployFile("b", "02_b.sql", new[] { "OBJ:B" }),
        };
        var deployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Exec(string batch) {
            if (batch.Contains("NEEDS:")) {
                var dep = batch.Split(new[] { "NEEDS:" }, StringSplitOptions.None)[1].Trim();
                if (!deployed.Contains(dep)) {
                    throw new InvalidOperationException("missing dependency " + dep);
                }
            }
            var obj = batch.Split(new[] { "OBJ:" }, StringSplitOptions.None)[1].Split(' ')[0];
            deployed.Add(obj);
        }
        var result = DeployRunner.Run(files, Exec);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.Files.First(f => f.Name == "01_a.sql").Pass);
        Assert.AreEqual(1, result.Files.First(f => f.Name == "02_b.sql").Pass);
    }

    [TestMethod]
    public void Unresolvable_file_is_reported_failed() {
        var files = new List<DeployFile> {
            new DeployFile("a", "a.sql", new[] { "OBJ:A NEEDS:MISSING" }),
        };
        void Exec(string batch) => throw new InvalidOperationException("bad");
        var result = DeployRunner.Run(files, Exec);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(DeployState.Failed, result.Files[0].State);
        StringAssert.Contains(result.Files[0].Error, "bad");
    }
}

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
