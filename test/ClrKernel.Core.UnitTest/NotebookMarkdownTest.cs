using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The .nb.md reader/writer behind the web editor. The load-bearing property is
/// byte equality, not idempotence: every save in the web UI is a git commit, and a
/// commit that rewrites so much as a blank line invalidates the notebook's
/// promotion evidence (Promotion.UnchangedBetween compares the run's commit
/// against dev). So opening a notebook and saving it unchanged must produce the
/// same bytes.
/// </summary>
[TestClass]
public class NotebookMarkdownTest {
    // The shipped languages as data — this project deliberately references no
    // Language.* package, and descriptors are just values.
    private static IReadOnlyList<LanguageDescriptor> Languages() => new[] {
        Descriptor("http", "#!http", new[] { "http" }),
        Descriptor("mermaid", "#!mermaid", new[] { "mermaid" }),
        Descriptor("powershell", "#!pwsh", new[] { "pwsh", "powershell", "ps1" }, "#!powershell", "#!pwsh-connect"),
        Descriptor("shellscript", "#!bash", new[] { "bash", "zsh", "sh", "shell" }, "#!zsh", "#!sh", "#!shell", "#!shell-connect"),
        Descriptor("sql", "#!sql", new[] { "sql", "tsql" }, "#!sql-connect", "#!sql-bulk", "#!sql-merge", "#!sql-run", "#!sql-deploy"),
        Descriptor("oraclesql", "#!oraclesql", new[] { "oraclesql", "plsql" }),
        Descriptor("ansisql", "#!ansisql", new[] { "ansisql" }),
        Descriptor("dax", "#!dax", new[] { "dax" }, "#!dax-connect"),
    };

    [TestMethod]
    public void A_dialect_block_round_trips_and_runs_as_its_own_dialect() {
        // A new tag is a new fence and nothing else: the reader and writer are
        // driven by the descriptors, so adding a dialect needed no change here.
        const string source =
            "# Report\n\n```oraclesql\nSELECT * FROM DUAL\n```\n\n```sql\nSELECT 1\n```\n";

        var cells = NotebookMarkdown.Parse(source, Languages());

        Assert.AreEqual(3, cells.Count);
        Assert.AreEqual("oraclesql", cells[1].Tag);
        Assert.AreEqual("sql", cells[2].Tag);
        Assert.AreEqual(source, NotebookMarkdown.Serialize(cells), "byte-identical after a round trip");

        // And each runs as itself. The selector is prepended at run time and never
        // written to disk, so a dialect cell reaches its own language.
        StringAssert.StartsWith(
            NotebookMarkdown.ExecutableSource(cells[1], Languages()), "#!oraclesql\n");
        StringAssert.StartsWith(
            NotebookMarkdown.ExecutableSource(cells[2], Languages()), "#!sql\n");
    }

    [TestMethod]
    public void The_tsql_tag_still_belongs_to_the_dialect_that_has_always_had_it() {
        // The acceptance criterion, from the file's point of view: ```sql and
        // ```tsql are T-SQL, exactly as they were, and no new dialect took them.
        Assert.AreEqual("sql", NotebookMarkdown.LanguageForTag("sql", Languages())?.Id);
        Assert.AreEqual("sql", NotebookMarkdown.LanguageForTag("tsql", Languages())?.Id);

        // A new cell in each dialect gets the tag named after it.
        Assert.AreEqual("sql", NotebookMarkdown.TagFor(Languages().Single(l => l.Id == "sql")));
        Assert.AreEqual("oraclesql", NotebookMarkdown.TagFor(Languages().Single(l => l.Id == "oraclesql")));
        Assert.AreEqual("ansisql", NotebookMarkdown.TagFor(Languages().Single(l => l.Id == "ansisql")));
    }

    private static LanguageDescriptor Descriptor(
        string id, string defaultSelector, string[] tags, params string[] extraSelectors) => new() {
            Id = id,
            DisplayName = id,
            DefaultSelector = defaultSelector,
            Selectors = new[] { defaultSelector }.Concat(extraSelectors).ToList(),
            LanguageTags = tags,
        };

    private static string SamplesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "samples");

    public static IEnumerable<object[]> Samples =>
        Directory.EnumerateFiles(SamplesDirectory, "*.nb.md")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { Path.GetFileName(p) });

    [TestMethod]
    [DynamicData(nameof(Samples))]
    public void Every_shipped_notebook_round_trips_byte_for_byte(string name) {
        var text = File.ReadAllText(Path.Combine(SamplesDirectory, name));
        var cells = NotebookMarkdown.Parse(text, Languages());

        Assert.AreEqual(text, NotebookMarkdown.Serialize(cells, NotebookMarkdown.NewlineOf(text)),
            $"{name} does not survive Parse → Serialize; saving it from the web editor would rewrite the file");
    }

    /// <summary>
    /// The same corpus with Windows line endings, which is what git checks out on
    /// Windows and therefore what the file on that machine actually is.
    ///
    /// <para>
    /// Reading the file from disk only tests this on a Windows machine; converting
    /// it here tests it everywhere. It went unnoticed until the suite first ran on
    /// Windows, where every sample failed by exactly its own line count.
    /// </para>
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Samples))]
    public void A_notebook_with_windows_line_endings_round_trips_byte_for_byte(string name) {
        var text = File.ReadAllText(Path.Combine(SamplesDirectory, name))
            .Replace("\r\n", "\n").Replace("\n", "\r\n");
        var cells = NotebookMarkdown.Parse(text, Languages());

        Assert.AreEqual(text, NotebookMarkdown.Serialize(cells, NotebookMarkdown.NewlineOf(text)),
            $"{name} with CRLF does not survive Parse → Serialize; saving it from the web "
            + "editor on Windows would rewrite every line in the file");
    }

    [TestMethod]
    public void The_corpus_is_actually_being_parsed_into_cells() {
        // Guards the round-trip test above from passing vacuously: if everything
        // landed in one markdown cell it would round-trip trivially and prove nothing.
        var text = File.ReadAllText(Path.Combine(SamplesDirectory, "Shell.nb.md"));
        var cells = NotebookMarkdown.Parse(text, Languages());

        Assert.IsTrue(cells.Count(c => c.Kind == CellKind.Code) >= 3);
        Assert.IsTrue(cells.Any(c => c.Kind == CellKind.Markdown));
    }

    [TestMethod]
    public void A_tag_is_preserved_as_written_never_normalized() {
        // One shellscript language claims bash, zsh, sh and shell. Rewriting zsh to
        // the language's default would change which shell the cell runs in.
        var text = "```zsh\necho hi\n```\n\n```sh\necho there\n```\n\n```bash\necho again\n```\n";
        var cells = NotebookMarkdown.Parse(text, Languages());

        CollectionAssert.AreEqual(new[] { "zsh", "sh", "bash" }, cells.Select(c => c.Tag).ToArray());
        Assert.AreEqual(text, NotebookMarkdown.Serialize(cells));
    }

    [TestMethod]
    public void An_unknown_language_block_stays_prose_delimiters_included() {
        var text = "# Title\n\n```json\n{ \"a\": 1 }\n```\n\n```csharp\nvar x = 1;\n```\n";
        var cells = NotebookMarkdown.Parse(text, Languages());

        Assert.AreEqual(2, cells.Count);
        Assert.AreEqual(CellKind.Markdown, cells[0].Kind);
        StringAssert.Contains(cells[0].Source, "```json", "the block is prose, delimiters and all");
        Assert.AreEqual(CellKind.Code, cells[1].Kind);
        Assert.AreEqual(text, NotebookMarkdown.Serialize(cells));
    }

    [TestMethod]
    public void Bodies_and_blocks_survive_the_cases_the_execution_parser_drops() {
        // The execution view trims prose, drops empty blocks and loses an
        // unterminated one. The editing view keeps all three — dropping a cell on
        // load would delete a user's code on the next save.
        var empty = NotebookMarkdown.Parse("```sql\n```\n", Languages());
        Assert.AreEqual(1, empty.Count);
        Assert.AreEqual(string.Empty, empty[0].Source);

        var unterminated = NotebookMarkdown.Parse("```csharp\nvar x = 1;\n", Languages());
        Assert.AreEqual(1, unterminated.Count);
        Assert.AreEqual("var x = 1;", unterminated[0].Source);
        Assert.IsFalse(unterminated[0].Closed);
        Assert.AreEqual("```csharp\nvar x = 1;\n", NotebookMarkdown.Serialize(unterminated));

        // Blank lines inside a block belong to the code.
        var spaced = NotebookMarkdown.Parse("```csharp\nvar a = 1;\n\nvar b = 2;\n```\n", Languages());
        Assert.AreEqual("var a = 1;\n\nvar b = 2;", spaced[0].Source);
    }

    [TestMethod]
    public void A_tilde_delimiter_survives_the_round_trip() {
        var text = "~~~csharp\nvar s = \"```not a delimiter```\";\n~~~\n";
        var cells = NotebookMarkdown.Parse(text, Languages());

        Assert.AreEqual(1, cells.Count);
        StringAssert.Contains(cells[0].Source, "not a delimiter");
        Assert.AreEqual(text, NotebookMarkdown.Serialize(cells));
    }

    [TestMethod]
    public void Tag_for_a_new_cell_prefers_the_language_own_name() {
        var languages = Languages();
        Assert.AreEqual("sql", NotebookMarkdown.TagFor(languages.Single(l => l.Id == "sql")));
        Assert.AreEqual("powershell", NotebookMarkdown.TagFor(languages.Single(l => l.Id == "powershell")));
        // shellscript is not one of its own tags: the first claimed tag wins.
        Assert.AreEqual("bash", NotebookMarkdown.TagFor(languages.Single(l => l.Id == "shellscript")));
        Assert.AreEqual("csharp", NotebookMarkdown.TagFor(null));
    }

    [TestMethod]
    public void Executable_source_adds_the_selector_that_the_file_does_not_carry() {
        var languages = Languages();
        var sql = MarkdownCell.Code("sql", "SELECT 1");
        Assert.AreEqual("#!sql\nSELECT 1", NotebookMarkdown.ExecutableSource(sql, languages));

        // A tag with its own selector keeps it; an already-selectored body is left alone.
        Assert.AreEqual("#!zsh\necho hi",
            NotebookMarkdown.ExecutableSource(MarkdownCell.Code("zsh", "echo hi"), languages));
        Assert.AreEqual("#!sql-connect --name dw",
            NotebookMarkdown.ExecutableSource(MarkdownCell.Code("sql", "#!sql-connect --name dw"), languages));

        // C# and unknown tags run verbatim.
        Assert.AreEqual("var x = 1;",
            NotebookMarkdown.ExecutableSource(MarkdownCell.Code("csharp", "var x = 1;"), languages));
    }

    [TestMethod]
    public void Round_trip_is_stable_across_a_second_pass() {
        var text = File.ReadAllText(Path.Combine(SamplesDirectory, "SqlEtl.nb.md"));
        var once = NotebookMarkdown.Serialize(NotebookMarkdown.Parse(text, Languages()));
        var twice = NotebookMarkdown.Serialize(NotebookMarkdown.Parse(once, Languages()));

        Assert.AreEqual(once, twice);
    }
}
