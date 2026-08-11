using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Mermaid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class MermaidTest {
    [TestMethod]
    public void Library_is_embedded_and_nonempty() {
        var js = MermaidRenderer.LibraryJs;
        Assert.IsTrue(js.Length > 500_000, "embedded mermaid library looks too small: " + js.Length);
        StringAssert.Contains(js, "mermaid");
    }

    [TestMethod]
    public void RenderHtml_is_self_contained_and_inlines_the_library() {
        var html = MermaidRenderer.RenderHtml("graph TD; A-->B");
        StringAssert.Contains(html, "clrkernel-mermaid");
        StringAssert.Contains(html, "mermaid.initialize");     // inline init
        StringAssert.Contains(html, "<script>");               // inline library + init
        // The whole library is inlined, so the output is large and needs no
        // network: there is no external <script src> and no CDN reference.
        Assert.IsTrue(html.Length > 1_000_000, "library does not appear to be inlined: " + html.Length);
        Assert.IsFalse(html.Contains("<script src="), "output should not load an external script");
        Assert.IsFalse(html.Contains("cdn.jsdelivr.net"), "should not reference a CDN");
    }

    [TestMethod]
    public void RenderHtml_escapes_the_source_so_arrows_survive() {
        var html = MermaidRenderer.RenderHtml("graph TD; A-->B & C<D");
        // Source lives HTML-escaped in a hidden node (read back via textContent).
        StringAssert.Contains(html, "A--&gt;B");
        StringAssert.Contains(html, "C&lt;D");
    }

    [TestMethod]
    public void Render_bundles_html_and_plain_source() {
        var dd = MermaidRenderer.Render("pie title Pets\n \"Dogs\" : 3");
        StringAssert.Contains((string)dd.Data["text/html"], "clrkernel-mermaid");
        Assert.AreEqual("pie title Pets\n \"Dogs\" : 3", (string)dd.Data["text/plain"]);
    }

    [TestMethod]
    public void DisplayMermaid_helper_emits_html_display_data() {
        DisplayData captured = null;
        var previous = DisplayDataEmitter.DisplayDataHandler;
        DisplayDataEmitter.DisplayDataHandler = d => captured = d;
        try {
            "sequenceDiagram\n  A->>B: hi".DisplayMermaid();
        } finally {
            DisplayDataEmitter.DisplayDataHandler = previous;
        }
        Assert.IsNotNull(captured);
        StringAssert.Contains((string)captured.Data["text/html"], "clrkernel-mermaid");
    }

    [TestMethod]
    public async Task Engine_routes_mermaid_selector_cell_to_renderer() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var result = await engine.ExecuteAsync("#!mermaid\ngraph LR; A-->B");

        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "mermaid cell should return display data");
        StringAssert.Contains((string)dd.Data["text/html"], "clrkernel-mermaid");
        StringAssert.Contains((string)dd.Data["text/plain"], "graph LR; A-->B");
    }

    [TestMethod]
    public void Markdown_mermaid_fence_becomes_mermaid_block() {
        var md = "# Title\n\n```mermaid\ngraph TD; A-->B\n```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!mermaid\n");
        StringAssert.Contains(blocks[0], "graph TD; A-->B");
    }

    [TestMethod]
    public void Dib_mermaid_section_becomes_mermaid_block() {
        var dib = "#!csharp\nvar x = 1;\n#!mermaid\ngraph TD; A-->B\n";
        var blocks = NotebookImporter.ParseDib(dib);
        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("var x = 1;", blocks[0]);
        StringAssert.StartsWith(blocks[1], "#!mermaid\n");
        StringAssert.Contains(blocks[1], "graph TD; A-->B");
    }
}
