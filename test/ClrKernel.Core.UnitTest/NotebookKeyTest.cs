using ClrKernel.Core.ExtensionServer.Lsp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Which notebook a cell URI belongs to. This is the routing key for the whole
/// <c>lsp</c> surface — sessions, engines, completion context and connections all
/// hang off it — and it now has two callers that must agree: the VS Code extension
/// and the Jobs web editor. A URI that fails to parse does not error, it quietly
/// becomes its own notebook, so the shapes both clients send are pinned here.
/// </summary>
[TestClass]
public class NotebookKeyTest {
    [TestMethod]
    public void A_cell_uri_and_its_notebooks_own_uri_are_the_same_notebook() {
        Assert.AreEqual("/work/nb.md", LspServer.NotebookKeyFor("vscode-notebook-cell:/work/nb.md#c0"));
        Assert.AreEqual("/work/nb.md", LspServer.NotebookKeyFor("vscode-notebook-cell:/work/nb.md#c7"));
        Assert.AreEqual("/work/nb.md", LspServer.NotebookKeyFor("file:///work/nb.md"));
    }

    [TestMethod]
    public void An_escaped_path_is_unescaped_back_to_the_file() {
        // The Jobs editor builds cell URIs with Uri.AbsolutePath, so a notebook whose
        // name contains a space arrives percent-encoded. Unescaped, it is one notebook;
        // taken literally, every such notebook would be a second, unreachable session.
        Assert.AreEqual("/tmp/my notebooks/a b.nb.md",
            LspServer.NotebookKeyFor("vscode-notebook-cell:/tmp/my%20notebooks/a%20b.nb.md#c0"));
    }

    [TestMethod]
    public void A_bare_cell_id_is_its_own_key_which_is_why_ids_must_be_qualified() {
        // Not a bug — the harnesses depend on it — but it is the failure mode a client
        // hits by sending "c0" instead of a URI: no error, just a kernel per cell.
        Assert.AreEqual("c0", LspServer.NotebookKeyFor("c0"));
        Assert.AreEqual("c1", LspServer.NotebookKeyFor("c1"));
        Assert.AreEqual(string.Empty, LspServer.NotebookKeyFor(null));
    }
}
