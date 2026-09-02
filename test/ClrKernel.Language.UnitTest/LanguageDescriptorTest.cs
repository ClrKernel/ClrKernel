using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Dax;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Shell;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Language.UnitTest;

/// <summary>
/// Phase 2 of the language-provider registry: languages self-describe (display
/// name, language tags, directive tables) and completions/diagnostics are generated
/// from the same tables the parsers bind against — including the flags the old
/// hand-maintained completion lists had drifted away from.
/// </summary>
[TestClass]
public class LanguageDescriptorTest {
    /// <summary>
    /// The shipped set, from the registry the suite's composition root filled —
    /// not a list rebuilt here. A second copy of "what ships" is a copy that can
    /// disagree with the first, and the language a test forgot to add is exactly
    /// the language whose descriptor nobody checked.
    /// </summary>
    private static CellLanguageSet AllLanguages() => CellLanguageRegistry.Default.CreateSet();

    [TestMethod]
    public void A_selector_is_just_a_directive_name() {
        // One concept, not two: the routing tokens are derived from the directive
        // table, so a language cannot answer a selector it does not describe.
        foreach (var language in AllLanguages().Languages) {
            CollectionAssert.AreEqual(
                language.Directives.Select(d => d.Selector).ToList(), language.Selectors.ToList(),
                $"{language.Id}: selectors must be exactly its directive names");
            Assert.AreEqual(language.Directives[0].Selector, language.DefaultSelector,
                $"{language.Id}: the first directive is the default");
            foreach (var selector in language.Selectors) {
                StringAssert.StartsWith(selector, "#!", $"{language.Id}: a selector is #! + a name");
            }
        }
    }

    [TestMethod]
    public void Describe_carries_identity_tags_and_capabilities() {
        var descriptors = AllLanguages().Describe();
        Assert.AreEqual(8, descriptors.Count, "six languages, and SQL is three of them");

        var sql = descriptors.Single(d => d.Id == "sql");
        Assert.AreEqual("T-SQL", sql.DisplayName, "the button says which dialect, now that there are three");
        Assert.AreEqual("#!sql", sql.DefaultSelector);
        CollectionAssert.AreEqual(new[] { "sql", "tsql" }, sql.LanguageTags.ToList());
        Assert.IsTrue(sql.HasConnections);
        // Every dialect, not only T-SQL: they share one session and so share its
        // connections. Only `sql` declaring them left `#!ansisql` cells with no
        // connection button and a picker with nothing to offer.
        foreach (var id in new[] { "sql", "ansisql", "oraclesql" }) {
            Assert.IsTrue(descriptors.Single(d => d.Id == id).HasConnections, id);
        }
        Assert.IsTrue(sql.ConfigBacked, "SQL connections load from connections.json");
        Assert.AreEqual(6, sql.Directives.Count);

        var shell = descriptors.Single(d => d.Id == "shellscript");
        Assert.AreEqual("#!bash", shell.DefaultSelector, "bash is the shell default");
        CollectionAssert.AreEqual(new[] { "bash", "zsh", "sh", "shell" }, shell.LanguageTags.ToList());
        Assert.IsFalse(shell.HasConnections, "shell's SSH targets are session-local, not a catalog");

        var mermaid = descriptors.Single(d => d.Id == "mermaid");
        Assert.AreEqual("#!mermaid", mermaid.Directives.Single().Selector, "even a bare language describes its one directive");
        Assert.IsFalse(mermaid.HasConnections);

        Assert.IsTrue(descriptors.Single(d => d.Id == "dax").ConfigBacked);
        Assert.IsTrue(descriptors.Single(d => d.Id == "powershell").Directives
            .Any(d => d.Selector == "#!pwsh-connect"));
    }

    [TestMethod]
    public void The_sql_dialects_describe_themselves_as_dialects() {
        var descriptors = AllLanguages().Describe();
        var dialects = descriptors.Where(d => d.Category == "SQL").ToList();

        CollectionAssert.AreEquivalent(
            new[] { "sql", "oraclesql", "ansisql" }, dialects.Select(d => d.Id).ToList(),
            "the three cluster under one heading in a picker");
        CollectionAssert.AreEquivalent(
            new[] { "clr-sql", "clr-oraclesql", "clr-ansisql" },
            dialects.Select(d => d.EditorLanguageId).ToList(),
            "an editor id of its own each — it is what identifies a cell, so it cannot be shared");
        Assert.IsTrue(dialects.All(d => d.GrammarId == "sql"),
            "and one highlighter between them, which is about appearance and can be");

        // The compatibility declaration: which connection types can carry each
        // dialect's statements. Providers, not dialects — a cell does not change
        // language when it is pointed at a different connection.
        CollectionAssert.AreEqual(new[] { "SqlServer", "Odbc", "Jdbc" },
            descriptors.Single(d => d.Id == "sql").SupportedProviders.ToList());
        CollectionAssert.AreEqual(new[] { "Oracle", "Odbc", "Jdbc" },
            descriptors.Single(d => d.Id == "oraclesql").SupportedProviders.ToList());
        // Postgres is here and not under a dialect of its own: it is a first-party
        // provider that no SQL cell could run on, so a PostgreSQL connection was
        // reachable from C# and from the query editor and refused by every cell.
        CollectionAssert.AreEqual(new[] { "Postgres", "Odbc", "Jdbc" },
            descriptors.Single(d => d.Id == "ansisql").SupportedProviders.ToList());

        // The property behind all three, stated once: every first-party provider
        // that speaks SQL is carried by some dialect. A provider no dialect claims
        // is one nothing can query, which is how Postgres came to be missing.
        var carried = descriptors
            .SelectMany(d => d.SupportedProviders ?? System.Array.Empty<string>())
            .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var provider in new[] { "SqlServer", "Oracle", "Postgres", "Odbc", "Jdbc" }) {
            CollectionAssert.Contains(carried, provider,
                $"{provider} connections would be refused by every SQL cell.");
        }
    }

    [TestMethod]
    public void Every_language_names_itself_in_four_characters_or_fewer() {
        // The chip beside a cell in a contents list. Its whole job is that a
        // notebook mixing several languages is scannable, which fails the moment
        // two of them look the same or one is too long to fit.
        var descriptors = AllLanguages().Describe();
        foreach (var descriptor in descriptors) {
            Assert.IsFalse(string.IsNullOrWhiteSpace(descriptor.Monogram), descriptor.Id);
            Assert.IsTrue(descriptor.Monogram.Length <= 4,
                $"{descriptor.Id}: '{descriptor.Monogram}' does not fit a chip");
        }

        var monograms = descriptors.Select(d => d.Monogram).ToList();
        CollectionAssert.AreEqual(monograms.Distinct().ToList(), monograms,
            "two languages wearing one monogram is a chip that says nothing");

        // The pair the dialect split exists for.
        Assert.AreEqual("TSQL", descriptors.Single(d => d.Id == "sql").Monogram);
        Assert.AreEqual("ORA", descriptors.Single(d => d.Id == "oraclesql").Monogram);
        Assert.AreEqual("SQL", descriptors.Single(d => d.Id == "ansisql").Monogram);
    }

    [TestMethod]
    public void A_language_that_says_nothing_gets_its_id_cut_to_four() {
        // The default, for a language plugged in at run time by a third party.
        // Right for a short id and wrong for a long one, which is why the shipped
        // languages with long ids say what they want instead.
        Assert.AreEqual("HTTP", AllLanguages().Describe().Single(d => d.Id == "http").Monogram);
        Assert.AreEqual("DAX", AllLanguages().Describe().Single(d => d.Id == "dax").Monogram);
        Assert.AreEqual("MMD", AllLanguages().Describe().Single(d => d.Id == "mermaid").Monogram,
            "not 'MERM'");
        Assert.AreEqual("SH", AllLanguages().Describe().Single(d => d.Id == "shellscript").Monogram,
            "not 'SHEL'");
        Assert.AreEqual("PS", AllLanguages().Describe().Single(d => d.Id == "powershell").Monogram,
            "not 'POWE'");
    }

    [TestMethod]
    public void A_language_that_is_not_a_dialect_needed_no_change_to_say_so() {
        // The point of defaulting every new member: HTTP, Mermaid and the shells
        // were not edited, and they answer sensibly anyway.
        foreach (var id in new[] { "http", "mermaid", "shellscript", "powershell" }) {
            var descriptor = AllLanguages().Describe().Single(d => d.Id == id);
            Assert.IsNull(descriptor.Category, $"{id} belongs to no group");
            Assert.AreEqual(0, descriptor.SupportedProviders.Count,
                $"{id} is not provider-bound, which is not the same as running on anything");
            Assert.AreEqual(id, descriptor.EditorLanguageId, $"{id} is its own editor language");
            Assert.IsNull(descriptor.GrammarId, $"{id} is highlighted as itself");
        }
    }

    [TestMethod]
    public void Selector_for_tag_prefers_the_tag_own_selector() {
        var descriptors = AllLanguages().Describe();
        var shell = descriptors.Single(d => d.Id == "shellscript");
        Assert.AreEqual("#!zsh", shell.SelectorForTag("zsh"));
        Assert.AreEqual("#!bash", shell.SelectorForTag("bash"));

        var sql = descriptors.Single(d => d.Id == "sql");
        Assert.AreEqual("#!sql", sql.SelectorForTag("tsql"), "no #!tsql selector: fall back to the default");

        var pwsh = descriptors.Single(d => d.Id == "powershell");
        Assert.AreEqual("#!pwsh", pwsh.SelectorForTag("ps1"));
    }

    // ---- completion drift fixes: flags the parsers accept now complete ----

    private static System.Collections.Generic.List<string> SqlLabels(string code) =>
        SqlLanguage.Complete(code, code.Length, new SqlCompletionContext()).Items.Select(i => i.Label).ToList();

    private static System.Collections.Generic.List<string> DaxLabels(string code) =>
        DaxLanguage.Complete(code, code.Length, new DaxCompletionContext()).Items.Select(i => i.Label).ToList();

    [TestMethod]
    public void Sql_connect_completion_offers_every_parsed_flag_but_never_password() {
        var labels = SqlLabels("#!sql-connect --");
        foreach (var flag in new[] { "--provider", "--option", "--var", "--no-var", "--name", "--auth", "--trust-cert" }) {
            CollectionAssert.Contains(labels, flag);
        }
        CollectionAssert.DoesNotContain(labels, "--password", "forbidden flags stay unadvertised");
    }

    [TestMethod]
    public void Sql_bulk_and_merge_completion_no_longer_drift() {
        CollectionAssert.Contains(SqlLabels("#!sql-bulk --"), "--notify-after");
        CollectionAssert.Contains(SqlLabels("#!sql-merge --"), "--source-is-query");
    }

    [TestMethod]
    public void Dax_connect_completion_gains_integrated_and_auth_values() {
        var flags = DaxLabels("#!dax-connect --");
        CollectionAssert.Contains(flags, "--integrated");
        CollectionAssert.Contains(flags, "--model");
        CollectionAssert.DoesNotContain(flags, "--password");

        var auth = DaxLabels("#!dax-connect --auth ");
        CollectionAssert.Contains(auth, "entra");
        CollectionAssert.Contains(auth, "integrated");
    }

    // ---- directive-line diagnostics: bad flags surface before run time ----

    [TestMethod]
    public void Sql_diagnostics_flag_a_bad_directive_line() {
        var services = new SqlCellLanguage().Services;
        var diagnostics = services.Diagnose("#!sql-connect --name c --bogus x\nselect 1");
        Assert.IsTrue(diagnostics.Any(d => d.Message == "Unknown #!sql-connect flag '--bogus'." && d.Line == 1));

        Assert.IsFalse(services.Diagnose("#!sql-connect --name c --server s\nselect 1")
            .Any(d => d.Message.Contains("#!sql-connect")), "a valid connect line is clean");
    }

    // ---- connection providers: per-language lookup, Ssh shared by two languages ----

    [TestMethod]
    public void Connection_providers_resolve_per_language_and_ssh_serves_two() {
        var engine = new ClrKernel.Core.Scripting.InteractiveScriptEngine(
            System.Environment.CurrentDirectory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.AreEqual("SqlServer", engine.ConnectionProvidersFor("sql").Single().Type);
        Assert.AreEqual("AnalysisServices", engine.ConnectionProvidersFor("dax").Single().Type);
        // One "$type": "Ssh" host definition serves both languages — the lookup is
        // by language, never $type → one provider.
        Assert.IsTrue(engine.ConnectionProvidersFor("shellscript").Any(p => p.Type == "Ssh"));
        Assert.IsTrue(engine.ConnectionProvidersFor("powershell").Any(p => p.Type == "Ssh"));
        Assert.IsTrue(engine.ConnectionProvidersFor("powershell").Any(p => p.Type == "PSRemoting"));
        Assert.AreEqual(0, engine.ConnectionProvidersFor("mermaid").Count());
    }

    [TestMethod]
    public void Connect_selector_flags_exist_in_the_language_directive_tables() {
        // The wizard composes directive lines from DirectiveFlag values; every one
        // must be a real parameter of the provider's connect directive.
        var byLanguage = AllLanguages().Languages.ToDictionary(l => l.Id);
        foreach (var descriptor in new[] {
            ClrKernel.Database.Provider.SqlServer.SqlServerConnectionProvider.Descriptor,
            ClrKernel.Database.Provider.AnalysisServices.SsasConnectionProvider.Descriptor,
            SshConnectionProvider.Descriptor,
            PwshConnectionProvider.Descriptor,
        }) {
            var language = byLanguage[descriptor.LanguageIds[0]];
            var directive = language.Directives.Single(d => d.Selector == descriptor.ConnectSelector);
            foreach (var setting in descriptor.Settings.Where(s => s.DirectiveFlag != null)) {
                Assert.IsNotNull(directive.Find(setting.DirectiveFlag),
                    $"{descriptor.Type}: {setting.DirectiveFlag} is not a flag of {descriptor.ConnectSelector}");
            }
        }
    }

    [TestMethod]
    public void Dax_diagnostics_flag_a_bad_directive_line() {
        var services = new DaxCellLanguage().Services;
        var diagnostics = services.Diagnose("#!dax --nope\nEVALUATE T");
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual("Unknown #!dax flag '--nope'.", diagnostics[0].Message);

        Assert.AreEqual(0, services.Diagnose("#!dax --connections sales\nEVALUATE T").Count);
    }
}
