using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Reading a notebook for the connections it names.
/// <para>
/// The language is built here rather than imported, deliberately: Jobs does not
/// reference <c>Language.Sql</c> and must not need to. If these pass against a
/// hand-made descriptor, the extraction is being driven by what the kernel says
/// about a language rather than by anything it knows about SQL.
/// </para>
/// </summary>
[TestClass]
public class ConnectionReferencesTest {
    private static readonly LanguageDescriptor _sql = new() {
        Id = "sql",
        DisplayName = "SQL",
        DefaultSelector = "#!sql",
        Selectors = new[] { "#!sql", "#!sql-connect" },
        LanguageTags = new[] { "sql" },
        Directives = new[] {
            new DirectiveDefinition {
                Selector = "#!sql",
                Parameters = new[] {
                    new DirectiveParameter {
                        Name = "--connections",
                        Aliases = new[] { "--connection", "-c" },
                        ValueRole = "connection",
                    },
                },
            },
            new DirectiveDefinition {
                Selector = "#!sql-connect",
                Parameters = new[] {
                    new DirectiveParameter { Name = "--name", Aliases = new[] { "-n" }, Required = true },
                    new DirectiveParameter { Name = "--server", Aliases = new[] { "--host", "-s" } },
                    new DirectiveParameter { Name = "--database", Aliases = new[] { "-d" } },
                    new DirectiveParameter { Name = "--secret" },
                    new DirectiveParameter { Name = "--default", Kind = DirectiveParameterKind.Flag },
                },
            },
        },
    };

    private static readonly IReadOnlyList<LanguageDescriptor> _languages = new[] { _sql };

    private static readonly IReadOnlyList<ConnectionProviderDescriptor> _providers =
        new[] { SqlServerConnectionProvider.Descriptor };

    private static IReadOnlyList<string> Read(string notebook) =>
        ConnectionReferences.In(notebook, _languages, _providers);

    private static string Cell(string body) => "```sql\n" + body + "\n```\n";

    [TestMethod]
    public void AConnectDirectiveCarryingOnlyANameRefersToASavedConnection() {
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql-connect --name warehouse")).ToArray());
    }

    [TestMethod]
    public void SoDoesTheShortSpelling() {
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql-connect -n warehouse")).ToArray());
    }

    [TestMethod]
    public void AndMakingItTheDefaultIsStillOnlyReferringToIt() {
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql-connect --name warehouse --default")).ToArray());
    }

    [TestMethod]
    public void ADirectiveThatShapesAConnectionIsDefiningOneRatherThanNamingASavedOne() {
        // This notebook carries its own settings. Whatever it calls the connection,
        // it is not asking for anybody's saved entry, so blocking it would be wrong.
        Assert.AreEqual(0, Read(Cell(
            "#!sql-connect --name warehouse --server dw.db.local --secret sql:warehouse")).Count);
    }

    [TestMethod]
    public void ARunDirectiveNamesTheConnectionItRunsOn() {
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql --connection warehouse\nSELECT 1")).ToArray());
    }

    [TestMethod]
    public void TheShortRunSpellingToo() {
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql -c warehouse\nSELECT 1")).ToArray());
    }

    [TestMethod]
    public void ARunDirectiveOnTheDefaultConnectionNamesNothing() {
        Assert.AreEqual(0, Read(Cell("#!sql\nSELECT 1")).Count);
    }

    [TestMethod]
    public void EveryCellIsRead() {
        var notebook = Cell("#!sql-connect --name warehouse")
            + "Some prose about it.\n\n"
            + Cell("#!sql --connection reporting\nSELECT 1");
        CollectionAssert.AreEqual(new[] { "warehouse", "reporting" }, Read(notebook).ToArray());
    }

    [TestMethod]
    public void ANameIsReportedOnceHoweverOftenItAppears() {
        var notebook = Cell("#!sql-connect --name warehouse") + Cell("#!sql -c warehouse\nSELECT 1");
        CollectionAssert.AreEqual(new[] { "warehouse" }, Read(notebook).ToArray());
    }

    [TestMethod]
    public void ProseIsNotADirective() {
        // A markdown cell can say anything, including something that looks like one.
        Assert.AreEqual(0, Read("Run `#!sql-connect --name warehouse` to connect.\n").Count);
    }

    [TestMethod]
    public void ADirectiveOfAnotherLanguageIsNotRead() {
        Assert.AreEqual(0, Read("```bash\n#!sql-connect --name warehouse\n```\n").Count);
    }

    [TestMethod]
    public void TheRunSelectorIsNotMistakenForTheConnectOne() {
        // #!sql is a prefix of #!sql-connect, and matching by prefix would read this
        // line with the wrong definition and find nothing.
        CollectionAssert.AreEqual(
            new[] { "warehouse" }, Read(Cell("#!sql-connect --name warehouse")).ToArray());
    }

    [TestMethod]
    public void AConnectDirectiveOfAProviderWeCannotReasonAboutIsLeftAlone() {
        // No descriptor for this selector, so there is no telling a reference from a
        // definition — and guessing wrong would block a promotion that was fine.
        var oracle = new LanguageDescriptor {
            Id = "oracle",
            LanguageTags = new[] { "oracle" },
            Selectors = new[] { "#!ora-connect" },
            Directives = new[] {
                new DirectiveDefinition {
                    Selector = "#!ora-connect",
                    Parameters = new[] {
                        new DirectiveParameter { Name = "--name", Required = true },
                    },
                },
            },
        };
        var found = ConnectionReferences.In(
            "```oracle\n#!ora-connect --name warehouse\n```\n",
            new[] { _sql, oracle },
            _providers);
        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public void AnEmptyNotebookNamesNothing() {
        Assert.AreEqual(0, Read(string.Empty).Count);
        Assert.AreEqual(0, ConnectionReferences.In(null, _languages, _providers).Count);
    }
}
