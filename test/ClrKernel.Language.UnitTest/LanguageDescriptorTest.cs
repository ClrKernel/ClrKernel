using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Dax;
using ClrKernel.Language.Http;
using ClrKernel.Language.Mermaid;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Shell;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Language.UnitTest;

/// <summary>
/// Phase 2 of the language-provider registry: languages self-describe (display
/// name, fence tags, directive tables) and completions/diagnostics are generated
/// from the same tables the parsers bind against — including the flags the old
/// hand-maintained completion lists had drifted away from.
/// </summary>
[TestClass]
public class LanguageDescriptorTest {
    private static CellLanguageSet AllLanguages() => new(new ICellLanguage[] {
        new HttpCellLanguage(), new MermaidCellLanguage(), new PowerShellCellLanguage(),
        new ShellCellLanguage(), new SqlCellLanguage(), new DaxCellLanguage(),
    });

    [TestMethod]
    public void Every_directive_selector_belongs_to_its_language() {
        foreach (var language in AllLanguages().Languages) {
            foreach (var directive in language.Directives) {
                CollectionAssert.Contains(language.Selectors.ToList(), directive.Selector,
                    $"{language.Id}: directive '{directive.Selector}' must be one of the language's selectors");
            }
        }
    }

    [TestMethod]
    public void Describe_carries_identity_tags_and_capabilities() {
        var descriptors = AllLanguages().Describe();
        Assert.AreEqual(6, descriptors.Count);

        var sql = descriptors.Single(d => d.Id == "sql");
        Assert.AreEqual("SQL", sql.DisplayName);
        Assert.AreEqual("#!sql", sql.DefaultSelector);
        CollectionAssert.AreEqual(new[] { "sql", "tsql" }, sql.LanguageTags.ToList());
        Assert.IsTrue(sql.HasConnections);
        Assert.IsTrue(sql.ConfigBacked, "SQL connections load from connections.json");
        Assert.AreEqual(6, sql.Directives.Count);

        var shell = descriptors.Single(d => d.Id == "shellscript");
        Assert.AreEqual("#!bash", shell.DefaultSelector, "bash is the shell default");
        CollectionAssert.AreEqual(new[] { "bash", "zsh", "sh", "shell" }, shell.LanguageTags.ToList());
        Assert.IsFalse(shell.HasConnections, "shell's SSH targets are session-local, not a catalog");

        var mermaid = descriptors.Single(d => d.Id == "mermaid");
        Assert.AreEqual(0, mermaid.Directives.Count);
        Assert.IsFalse(mermaid.HasConnections);

        Assert.IsTrue(descriptors.Single(d => d.Id == "dax").ConfigBacked);
        Assert.IsTrue(descriptors.Single(d => d.Id == "powershell").Directives
            .Any(d => d.Selector == "#!pwsh-connect"));
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
