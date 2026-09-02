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
    public void Cleanup() => TempDirectory.Delete(_root);

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
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());

        var readme = tree.Children.Single(c => c.Name == "readme.txt");
        Assert.AreEqual("file", readme.Kind);
        Assert.IsTrue(readme.Editable, "a .txt is text, and text is what this edits");
        Assert.IsFalse(tree.Children.Single(c => c.Name == "logo.png").Editable,
            "a picture opens to look at");

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
        // Text, whatever kind of text. The tool that opens it is a text editor.
        Assert.IsTrue(NotebookTree.IsEditable("settings.json"));
        Assert.IsTrue(NotebookTree.IsEditable("a/b.yaml"));
        Assert.IsTrue(NotebookTree.IsEditable("a/b.txt"));
        Assert.IsTrue(NotebookTree.IsEditable("a/b.md"));
        Assert.IsTrue(NotebookTree.IsEditable("a/logo.svg"), "an svg is a document as well as a picture");
        Assert.IsTrue(NotebookTree.IsEditable("a/.gitignore"), "a dot-file is still a file");
    }

    /// <summary>
    /// The half that stops the widening from being a hole. It is handed a resolved
    /// absolute path, so git's own storage arrives looking like ordinary files —
    /// <c>.git/config</c> has no extension and <c>.git/HEAD</c> is not a notebook,
    /// but <c>.git/description</c> would sail through a plain extension check.
    /// </summary>
    [TestMethod]
    public void Nothing_under_a_protected_name_is_editable() {
        Assert.IsFalse(NotebookTree.IsEditable("/w/mine/.git/config"));
        Assert.IsFalse(NotebookTree.IsEditable("/w/mine/.git/hooks/pre-commit.sh"));
        Assert.IsFalse(NotebookTree.IsEditable("/w/.repo.git/description"));
        // Not `.scratch`: it is hidden from the tree because it belongs to the
        // tool rather than to the project, and the query editor writes to it.
        Assert.IsTrue(NotebookTree.IsEditable("/w/mine/.scratch/query.sql"));
        Assert.IsFalse(NotebookTree.IsEditable("/w/mine/.nightly.nb.md.saving"));
        // And a picture is not text, however editable the folder it sits in is.
        Assert.IsFalse(NotebookTree.IsEditable("a/chart.png"));
        Assert.IsFalse(NotebookTree.IsEditable("a/report.pdf"));
        // Generated from the saved connections, so an edit here would be undone.
        Assert.IsFalse(NotebookTree.IsEditable("a/connections.json"));
        Assert.IsFalse(NotebookTree.IsEditable("a/connections.local.json"));
    }

    /// <summary>
    /// Dot-files show — a repo's `.gitignore` is a file somebody edits. What stays
    /// out is git's own storage (a worktree's `.git` is a *file*, so the rule has to
    /// be by name rather than by kind), the scratch buffer, OS junk, and the
    /// `.*.saving` staging file a crash mid-write leaves behind — which is half a
    /// notebook and must not look like one.
    /// </summary>
    [TestMethod]
    public void Git_storage_stays_out_of_the_tree_and_ordinary_dot_files_do_not() {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/");
        File.WriteAllText(Path.Combine(_root, ".DS_Store"), "junk");
        File.WriteAllText(Path.Combine(_root, "etl", ".nightly.nb.md.saving"), "half a fi");
        // What a worktree actually has at its root: a file, not a directory.
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: /w/.repo.git/worktrees/mine");
        Directory.CreateDirectory(Path.Combine(_root, ".scratch"));
        File.WriteAllText(Path.Combine(_root, ".scratch", "query.sql"), "SELECT 1");

        var tree = NotebookTree.Build(_root, new JobCatalog(_root).Load());
        var names = tree.Children.Select(c => c.Name).ToList();

        CollectionAssert.Contains(names, ".gitignore");
        Assert.IsTrue(tree.Children.Single(c => c.Name == ".gitignore").Editable);
        CollectionAssert.DoesNotContain(names, ".git");
        CollectionAssert.DoesNotContain(names, ".scratch");
        CollectionAssert.DoesNotContain(names, ".DS_Store");
        var etl = tree.Children.Single(c => c.IsDirectory && c.Name == "etl");
        Assert.IsFalse(etl.Children.Any(c => c.Name.EndsWith(".saving")));
    }
}
