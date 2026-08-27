using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The traversal guard is a trust boundary — every path the API accepts goes
/// through it — so it gets its own tests, plus the tree shape the UI renders.
/// </summary>
[TestClass]
public class NotebookTreeTest {
    private string _root;

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-tree-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "etl"));
        File.WriteAllText(Path.Combine(_root, "etl", "nightly.nb.md"), "```csharp\n1\n```\n");
        File.WriteAllText(Path.Combine(_root, "etl", "nightly.jobs.yaml"),
            "notebook: ./nightly.nb.md\njobs: [{name: nightly-us}, {name: nightly-eu}]");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "not a notebook");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void A_path_inside_the_root_resolves() {
        Assert.IsNotNull(NotebookTree.SafeResolve(_root, "etl/nightly.nb.md"));
        Assert.IsNotNull(NotebookTree.SafeResolve(_root, "./etl/nightly.nb.md"));
        Assert.IsNotNull(NotebookTree.SafeResolve(_root, "etl/../etl/nightly.nb.md"),
            "traversal that stays inside is fine");
    }

    [TestMethod]
    public void Traversal_out_of_the_root_is_rejected() {
        Assert.IsNull(NotebookTree.SafeResolve(_root, "../secrets.txt"));
        Assert.IsNull(NotebookTree.SafeResolve(_root, "etl/../../secrets.txt"));
        Assert.IsNull(NotebookTree.SafeResolve(_root, "etl/../../../../../../etc/passwd"));
    }

    [TestMethod]
    public void Absolute_paths_are_rejected() {
        Assert.IsNull(NotebookTree.SafeResolve(_root, "/etc/passwd"));
        Assert.IsNull(NotebookTree.SafeResolve(_root, "C:\\Windows\\win.ini"));
        Assert.IsNull(NotebookTree.SafeResolve(_root, string.Empty));
        Assert.IsNull(NotebookTree.SafeResolve(_root, null));
    }

    [TestMethod]
    public void A_symlink_pointing_outside_the_root_is_rejected() {
        var outside = Path.Combine(Path.GetTempPath(), "clrkernel-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        var link = Path.Combine(_root, "escape.nb.md");
        try {
            File.CreateSymbolicLink(link, outside);
        } catch (Exception) {
            Assert.Inconclusive("This platform/user cannot create symlinks.");
            return;
        }
        try {
            Assert.IsNull(NotebookTree.SafeResolve(_root, "escape.nb.md"));
        } finally {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [TestMethod]
    public void The_tree_lists_notebooks_with_their_jobs() {
        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());

        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.AreEqual("etl", etl.Name);
        CollectionAssert.AreEquivalent(
            new[] { "nightly.nb.md", "nightly.jobs.yaml" },
            etl.Children.Select(c => c.Name).ToArray());

        var notebook = etl.Children.Single(c => c.Kind == "notebook");
        Assert.AreEqual("etl/nightly.nb.md", notebook.Path);
        CollectionAssert.AreEquivalent(new[] { "nightly-eu", "nightly-us" }, notebook.Jobs.ToArray());
    }

    /// <summary>
    /// It is a browser over the project now, not a notebook list. Everything shows;
    /// what differs is whether it can be changed.
    /// </summary>
    [TestMethod]
    public void Every_file_is_listed_and_says_whether_it_can_be_edited() {
        File.WriteAllText(Path.Combine(_root, "etl", "query.sql"), "SELECT 1");
        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());

        var readme = tree.Children.Single(c => c.Name == "readme.txt");
        Assert.AreEqual("file", readme.Kind);
        Assert.IsFalse(readme.Editable, "a .txt is browsable, not writable");

        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.AreEqual("file", etl.Children.Single(c => c.Name == "query.sql").Kind);
        Assert.IsTrue(etl.Children.Single(c => c.Name == "nightly.nb.md").Editable);
        Assert.IsTrue(etl.Children.Single(c => c.Name == "nightly.jobs.yaml").Editable,
            "the whole point of the change: a jobs file is reachable and writable");
        Assert.AreEqual("jobs", etl.Children.Single(c => c.Name == "nightly.jobs.yaml").Kind);
    }

    /// <summary>
    /// The tree and the route that refuses the save have to agree, or the UI offers
    /// an edit the server will reject.
    /// </summary>
    [TestMethod]
    public void Editable_is_the_same_rule_the_write_route_enforces() {
        Assert.IsTrue(NotebookTree.IsEditable("a/b.nb.md"));
        Assert.IsTrue(NotebookTree.IsEditable("a/b.ipynb"));
        Assert.IsTrue(NotebookTree.IsEditable("a/b.JOBS.YAML"));
        Assert.IsFalse(NotebookTree.IsEditable("a/b.yaml"), "a plain yaml is not a jobs file");
        Assert.IsFalse(NotebookTree.IsEditable("a/b.txt"));
        Assert.IsFalse(NotebookTree.IsEditable("a/b.md"), "only .nb.md is a notebook");
    }

    /// <summary>
    /// Dot-files are noise: .DS_Store, and the `.*.saving` staging file a crash
    /// mid-write leaves behind — which is half a notebook and must not look like one.
    /// </summary>
    [TestMethod]
    public void Dot_files_stay_out_of_the_tree() {
        File.WriteAllText(Path.Combine(_root, ".DS_Store"), "junk");
        File.WriteAllText(Path.Combine(_root, "etl", ".nightly.nb.md.saving"), "half a fi");
        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());

        Assert.IsFalse(tree.Children.Any(c => c.Name.StartsWith('.')));
        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.IsFalse(etl.Children.Any(c => c.Name.StartsWith('.')));
    }
}
