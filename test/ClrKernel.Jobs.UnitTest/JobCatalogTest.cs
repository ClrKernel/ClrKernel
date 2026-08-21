using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>Tree-wide validation: unique names, notebooks exist, DAG is sound.</summary>
[TestClass]
public class JobCatalogTest {
    private string _root;

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-catalog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content) {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    [TestMethod]
    public void A_valid_tree_loads_all_jobs_with_no_errors() {
        Write("etl/nb.nb.md", "# hi\n```csharp\n1+1\n```\n");
        Write("etl/nightly.jobs.yaml",
            """
            notebook: ./nb.nb.md
            jobs:
              - name: a
              - name: b
                dependsOn: [a]
            """);

        var result = new JobCatalog(_root).Load();
        Assert.AreEqual(0, result.Errors.Count, string.Join("; ", result.Errors));
        Assert.AreEqual(2, result.Jobs.Count);
        Assert.IsNotNull(result.Find("default", "A"), "job lookup is case-insensitive");
    }

    [TestMethod]
    public void Duplicate_names_across_files_are_reported() {
        Write("a/x.jobs.yaml", "notebook: ./nb.nb.md\njobs: [{name: dupe}]");
        Write("b/y.jobs.yaml", "notebook: ./nb.nb.md\njobs: [{name: dupe}]");
        Write("a/nb.nb.md", "x");
        Write("b/nb.nb.md", "x");

        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("duplicate job name 'dupe'")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void A_missing_notebook_is_reported() {
        Write("x.jobs.yaml", "notebook: ./missing.nb.md\njobs: [{name: ghost}]");
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("notebook not found")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void A_dependency_cycle_is_reported() {
        Write("nb.nb.md", "x");
        Write("x.jobs.yaml",
            """
            notebook: ./nb.nb.md
            jobs:
              - name: a
                dependsOn: [b]
              - name: b
                dependsOn: [a]
              - name: standalone
            """);
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("cycle") && e.Contains("a") && e.Contains("b")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void An_unknown_dependency_is_reported() {
        Write("nb.nb.md", "x");
        Write("x.jobs.yaml", "notebook: ./nb.nb.md\njobs: [{name: a, dependsOn: [nope]}]");
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("unknown job 'nope'")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void A_broken_yaml_file_reports_but_does_not_hide_other_files() {
        Write("nb.nb.md", "x");
        Write("good.jobs.yaml", "notebook: ./nb.nb.md\njobs: [{name: good}]");
        Write("bad.jobs.yaml", "jobs: [ {name: ");
        var result = new JobCatalog(_root).Load();
        Assert.IsNotNull(result.Find("default", "good"));
        Assert.IsTrue(result.Errors.Any(e => e.StartsWith("bad.jobs.yaml")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void Dependents_lookup_follows_the_graph() {
        var jobs = new[] {
            new JobDefinition { Name = "a" },
            new JobDefinition { Name = "b", DependsOn = new[] { "a" } },
            new JobDefinition { Name = "c", DependsOn = new[] { "a", "b" } },
        };
        var graph = new JobGraph(jobs);
        CollectionAssert.AreEquivalent(new[] { "b", "c" }, graph.DependentsOf("a").ToArray());
        CollectionAssert.AreEquivalent(new[] { "c" }, graph.DependentsOf("b").ToArray());
        Assert.AreEqual(0, graph.DependentsOf("c").Count);
        Assert.AreEqual(0, graph.Validate().Count);
    }
}
