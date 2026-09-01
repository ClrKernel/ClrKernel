using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// `clrkernel convert` — a .dib or .ipynb rewritten as executable markdown.
///
/// <para>
/// The property that matters is that the result *runs the same*. A converted cell
/// that lost its language is a cell that will execute as C# and fail, and a
/// converted cell that kept its `#!` selector line executes it twice. So every case
/// here checks the tag on the fence, and the round trip back through
/// <see cref="NotebookMarkdown"/> that the runner will do.
/// </para>
/// </summary>
[TestClass]
public class NotebookConverterTest {
    private static IReadOnlyList<LanguageDescriptor> Languages() => new[] {
        Descriptor("sql", "#!sql", new[] { "sql", "tsql" }),
        Descriptor("shellscript", "#!bash", new[] { "bash", "zsh", "sh", "shell" }, "#!zsh", "#!sh"),
        Descriptor("mermaid", "#!mermaid", new[] { "mermaid" }),
    };

    private static LanguageDescriptor Descriptor(
        string id, string defaultSelector, string[] tags, params string[] extraSelectors) => new() {
            Id = id,
            DisplayName = id,
            DefaultSelector = defaultSelector,
            Selectors = new[] { defaultSelector }.Concat(extraSelectors).ToList(),
            LanguageTags = tags,
        };

    [TestMethod]
    public void A_dib_becomes_fenced_blocks_that_keep_their_language() {
        const string dib = """
            #!csharp

            var x = 1;

            #!markdown

            Some prose.

            #!sql

            SELECT 1

            """;

        var markdown = NotebookConverter.ToMarkdown(dib, ".dib", Languages());

        StringAssert.Contains(markdown, "```csharp\nvar x = 1;\n```");
        StringAssert.Contains(markdown, "Some prose.");
        StringAssert.Contains(markdown, "```sql\nSELECT 1\n```");
        // The prose section is prose, not a fenced block claiming to be markdown.
        Assert.IsFalse(markdown.Contains("```markdown"), markdown);
        // And the selector is gone from the body: on a fence it would be executed
        // as part of the cell.
        Assert.IsFalse(markdown.Contains("#!sql"), markdown);
    }

    /// <summary>
    /// The one that pays for the whole "tag as written" rule: `zsh` and `bash` are
    /// the same language and different shells, so a converter that normalised them
    /// would change what the notebook does.
    /// </summary>
    [TestMethod]
    public void A_dib_section_keeps_the_shell_it_named() {
        var markdown = NotebookConverter.ToMarkdown(
            "#!zsh\n\necho hi\n", ".dib", Languages());

        StringAssert.Contains(markdown, "```zsh\n");
        Assert.IsFalse(markdown.Contains("```bash"), markdown);
    }

    [TestMethod]
    public void What_comes_out_parses_back_into_the_same_cells() {
        const string dib = "#!csharp\n\nvar x = 1;\n\n#!sql\n\nSELECT 1\n";

        var cells = NotebookMarkdown.Parse(
            NotebookConverter.ToMarkdown(dib, ".dib", Languages()), Languages());

        Assert.AreEqual(2, cells.Count);
        Assert.AreEqual("csharp", cells[0].Tag);
        Assert.AreEqual("sql", cells[1].Tag);
        // The runner puts the selector back at execution, so the sql cell still
        // reaches the sql language after the trip through markdown.
        StringAssert.StartsWith(NotebookMarkdown.ExecutableSource(cells[1], Languages()), "#!sql\n");
    }

    [TestMethod]
    public void An_ipynb_drops_its_outputs_and_keeps_its_prose() {
        const string ipynb = """
            {"cells":[
              {"cell_type":"markdown","source":["# Title\n","\n","Prose."]},
              {"cell_type":"code","source":["1 + 1"],
               "outputs":[{"output_type":"execute_result","data":{"text/plain":"2"}}],
               "execution_count":7}
            ],"metadata":{},"nbformat":4,"nbformat_minor":5}
            """;

        var markdown = NotebookConverter.ToMarkdown(ipynb, ".ipynb", Languages());

        StringAssert.Contains(markdown, "# Title");
        StringAssert.Contains(markdown, "```csharp\n1 + 1\n```");
        // Stored results are the thing being converted away from. A .nb.md that
        // carried them would not diff like source, which is the whole point.
        Assert.IsFalse(markdown.Contains("execute_result"), markdown);
        Assert.IsFalse(markdown.Contains("\"2\""), markdown);
    }

    /// <summary>
    /// A polyglot `.ipynb` — and the artifact `clrkernel run -o` writes — records the
    /// language as a selector line inside the cell's own source. It belongs on the
    /// fence, and only there.
    /// </summary>
    [TestMethod]
    public void An_ipynb_cell_that_names_its_language_gets_it_on_the_fence() {
        const string ipynb =
            """{"cells":[{"cell_type":"code","source":["#!sql\n","SELECT 1"]}],"nbformat":4}""";

        var markdown = NotebookConverter.ToMarkdown(ipynb, ".ipynb", Languages());

        StringAssert.Contains(markdown, "```sql\nSELECT 1\n```");
        Assert.IsFalse(markdown.Contains("#!sql"), "the selector moved, it was not copied");
    }

    [TestMethod]
    public void An_unknown_bang_line_is_left_in_the_cell() {
        // `#!time` is not a language this kernel knows; it may well be a magic the
        // cell means to run. Guessing it is a language would delete it.
        const string ipynb =
            """{"cells":[{"cell_type":"code","source":["#!time\n","work()"]}],"nbformat":4}""";

        var markdown = NotebookConverter.ToMarkdown(ipynb, ".ipynb", Languages());

        StringAssert.Contains(markdown, "```csharp\n#!time\nwork()\n```");
    }

    [TestMethod]
    public void A_csx_is_one_code_cell() =>
        Assert.AreEqual("```csharp\nConsole.WriteLine(1);\n```\n",
            NotebookConverter.ToMarkdown("Console.WriteLine(1);\n", ".csx", Languages()));

    [TestMethod]
    public void Converting_what_is_already_markdown_is_refused() {
        // Not a silent no-op: it would rewrite the file it was pointed at, and the
        // caller asked for something this cannot do.
        // `.md`, because that is what Path.GetExtension makes of "notes.nb.md" —
        // and answering with "cannot convert '.md'" would be talking about a
        // different file than the one on the command line.
        var e = Assert.ThrowsExactly<NotSupportedException>(
            () => NotebookConverter.ToMarkdown("# hi\n", ".md", Languages()));
        StringAssert.Contains(e.Message, "already executable markdown");

        var other = Assert.ThrowsExactly<NotSupportedException>(
            () => NotebookConverter.ToMarkdown("x", ".rmd", Languages()));
        StringAssert.Contains(other.Message, ".dib");
    }

    [TestMethod]
    public void The_default_output_sits_beside_its_input() {
        StringAssert.EndsWith(NotebookConverter.DefaultOutput("/tmp/notes.dib"), "notes.nb.md");
        StringAssert.EndsWith(NotebookConverter.DefaultOutput("reports/q3.ipynb"), "q3.nb.md");
    }
}
