using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

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
        Write("etl/nb.jobs.yaml",
            """
            jobs:
              - name: a
              - name: b
                dependsOn: [a]
            """);

        var result = new JobCatalog(_root).Load();
        Assert.AreEqual(0, result.Errors.Count, string.Join("; ", result.Errors));
        Assert.AreEqual(2, result.Jobs.Count);
        Assert.IsNotNull(result.Find("default", "default", "A"), "job lookup is case-insensitive");
    }

    [TestMethod]
    public void Duplicate_names_across_files_are_reported() {
        // Two files in two folders, each paired with its own notebook, both
        // defining `dupe` — names are unique per environment, not per folder.
        Write("a/nb.nb.md", "x");
        Write("b/nb.nb.md", "x");
        Write("a/nb.jobs.yaml", "jobs: [{name: dupe}]");
        Write("b/nb.jobs.yaml", "jobs: [{name: dupe}]");

        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("duplicate job name 'dupe'")),
            string.Join("; ", result.Errors));
    }

    /// <summary>
    /// A jobs file with no notebook beside it schedules nothing. It is reported
    /// rather than loaded, which is what keeps prod from holding a schedule whose
    /// notebook is missing.
    /// </summary>
    [TestMethod]
    public void A_jobs_file_with_no_paired_notebook_is_reported() {
        Write("ghost.jobs.yaml", "jobs: [{name: ghost}]");
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("No notebook beside this file")),
            string.Join("; ", result.Errors));
        Assert.AreEqual(0, result.Jobs.Count);
    }

    [TestMethod]
    public void A_declared_notebook_that_is_not_the_paired_one_is_reported() {
        Write("nb.nb.md", "x");
        Write("nb.jobs.yaml", "notebook: ./somewhere-else.nb.md\njobs: [{name: a}]");
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("not the notebook this file is named for")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void A_dependency_cycle_is_reported() {
        Write("nb.nb.md", "x");
        Write("nb.jobs.yaml",
            """
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
        Write("nb.jobs.yaml", "jobs: [{name: a, dependsOn: [nope]}]");
        var result = new JobCatalog(_root).Load();
        Assert.IsTrue(result.Errors.Any(e => e.Contains("unknown job 'nope'")),
            string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void A_broken_yaml_file_reports_but_does_not_hide_other_files() {
        Write("nb.nb.md", "x");
        Write("nb.jobs.yaml", "jobs: [{name: good}]");
        Write("bad.nb.md", "x");
        Write("bad.jobs.yaml", "jobs: [ {name: ");
        var result = new JobCatalog(_root).Load();
        Assert.IsNotNull(result.Find("default", "default", "good"));
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

    /// <summary>
    /// Deleting a jobs file stops its schedule without anything being told to
    /// reload: the catalog enumerates the tree on every Load and prunes what is
    /// gone, and the scheduler calls Load on every tick. This is the reason
    /// promoting a deletion needs no registry reload — the timer stops within a
    /// tick, not at the next restart.
    /// </summary>
    [TestMethod]
    public void A_deleted_jobs_file_stops_being_scheduled_on_the_next_load() {
        Write("nb.nb.md", "x");
        Write("nb.jobs.yaml", "jobs: [{name: nightly, cron: \"0 2 * * *\"}]");
        var catalog = new JobCatalog(_root);
        Assert.IsNotNull(catalog.Load().Find("default", "default", "nightly"));

        File.Delete(Path.Combine(_root, "nb.jobs.yaml"));

        var after = catalog.Load();
        Assert.IsNull(after.Find("default", "default", "nightly"),
            "the same catalog instance, no restart, no cache to invalidate by hand");
        Assert.AreEqual(0, after.Errors.Count, string.Join("; ", after.Errors));
    }

    /// <summary>The notebook surviving its jobs file is fine — it is unscheduled,
    /// not broken, and still runnable by hand.</summary>
    [TestMethod]
    public void A_notebook_with_no_jobs_file_is_not_an_error() {
        Write("nb.nb.md", "x");
        var result = new JobCatalog(_root).Load();
        Assert.AreEqual(0, result.Errors.Count, string.Join("; ", result.Errors));
        Assert.AreEqual(0, result.Jobs.Count);
    }

}
