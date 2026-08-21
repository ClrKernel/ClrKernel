using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

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
    public void The_tree_lists_notebooks_with_their_jobs_and_skips_other_files() {
        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());

        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.AreEqual("etl", etl.Name);
        CollectionAssert.AreEquivalent(
            new[] { "nightly.nb.md", "nightly.jobs.yaml" },
            etl.Children.Select(c => c.Name).ToArray());

        var notebook = etl.Children.Single(c => c.Kind == "notebook");
        Assert.AreEqual("etl/nightly.nb.md", notebook.Path);
        CollectionAssert.AreEquivalent(new[] { "nightly-eu", "nightly-us" }, notebook.Jobs.ToArray());
        Assert.IsFalse(tree.Children.Any(c => c.Name == "readme.txt"), "non-notebooks are skipped");
    }
}
