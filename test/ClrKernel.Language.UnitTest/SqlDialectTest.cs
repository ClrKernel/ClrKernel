using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// SQL as three dialects rather than one language.
/// <para>
/// The property under test throughout is the split the feature rests on: the
/// <b>dialect</b> is a property of the cell — it decides which words are legal —
/// and the <b>provider</b> is a property of the connection. Pointing a cell at a
/// different connection must never change what language it is written in, and a
/// pairing that cannot work has to say so in those terms rather than as a driver
/// parse error.
/// </para>
/// </summary>
[TestClass]
public class SqlDialectTest {
    private static InteractiveScriptEngine NewEngine() =>
        new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);

    private static CellLanguageSet Languages() => CellLanguageRegistry.Default.CreateSet();

    private static async Task<Exception> Raised(InteractiveScriptEngine engine, string cell) {
        try {
            await engine.ExecuteAsync(cell);
            return null;
        } catch (Exception e) {
            return e;
        }
    }

    // --- what the file says ---------------------------------------------------

    [TestMethod]
    public void The_sql_id_still_means_t_sql() {
        // The one thing that could not change. `sql` has meant T-SQL since the
        // first release; every notebook already written says it, and the dialects
        // took new ids rather than this one taking a new meaning.
        var sql = Languages().ById("sql");
        Assert.IsInstanceOfType<SqlCellLanguage>(sql);
        Assert.AreEqual("T-SQL", sql.DisplayName);
        CollectionAssert.AreEqual(new[] { "sql", "tsql" }, sql.LanguageTags.ToList());
        Assert.AreEqual("#!sql", sql.DefaultSelector);
    }

    [TestMethod]
    public void Each_dialect_claims_its_own_tags_and_selector() {
        var languages = Languages();
        var oracle = languages.ById("oraclesql");
        var generic = languages.ById("ansisql");

        Assert.AreEqual("#!oraclesql", oracle.DefaultSelector);
        CollectionAssert.AreEqual(new[] { "oraclesql", "plsql" }, oracle.LanguageTags.ToList());
        Assert.AreEqual("#!ansisql", generic.DefaultSelector);

        // No tag is claimed twice, or a fenced block would parse as whichever
        // language happened to be registered first.
        var tags = languages.Languages.SelectMany(l => l.LanguageTags).ToList();
        CollectionAssert.AreEquivalent(tags.Distinct().ToList(), tags, "no tag belongs to two languages");
    }

    [TestMethod]
    public async Task A_dialect_cell_routes_to_its_own_dialect() {
        var engine = NewEngine();

        // Nothing is connected, so each of these fails — but the message names the
        // dialect that answered, which is what says dispatch went where it should.
        var oracle = await Raised(engine, "#!oraclesql\nSELECT * FROM DUAL");
        StringAssert.Contains(oracle?.Message ?? "", "connection");

        var tsql = await Raised(engine, "#!sql\nSELECT 1");
        StringAssert.Contains(tsql?.Message ?? "", "connection");
    }

    // --- what each dialect knows ---------------------------------------------

    private static string[] Complete(string id, string code) {
        var language = Languages().ById(id);
        var result = language.Services
            .CompleteAsync(code, code.Length, new LanguageServiceContext()).GetAwaiter().GetResult();
        return result.Items.Select(i => i.Label).ToArray();
    }

    [TestMethod]
    public void A_dialect_completes_its_own_words_and_nobody_else_s() {
        // The acceptance criterion, stated as the thing that would be wrong: an
        // editor offering NVL in a T-SQL cell is asserting the statement will run.
        CollectionAssert.Contains(Complete("sql", "SELECT NV"), "NVARCHAR");
        CollectionAssert.DoesNotContain(Complete("sql", "SELECT NV"), "NVL");

        CollectionAssert.Contains(Complete("oraclesql", "SELECT NV"), "NVL");
        CollectionAssert.DoesNotContain(Complete("oraclesql", "SELECT NV"), "NVARCHAR");

        CollectionAssert.Contains(Complete("oraclesql", "SELECT SYSD"), "SYSDATE");
        CollectionAssert.DoesNotContain(Complete("sql", "SELECT SYSD"), "SYSDATE");
        CollectionAssert.Contains(Complete("sql", "SELECT GETD"), "GETDATE");
    }

    [TestMethod]
    public void Generic_sql_offers_only_what_is_standard() {
        var generic = Complete("ansisql", "SELECT ");
        CollectionAssert.Contains(generic, "COALESCE");
        CollectionAssert.Contains(generic, "EXTRACT");
        // Neither vendor's spelling of "the first non-null argument".
        CollectionAssert.DoesNotContain(generic, "ISNULL");
        CollectionAssert.DoesNotContain(generic, "NVL");
        CollectionAssert.DoesNotContain(generic, "GETDATE");
        CollectionAssert.DoesNotContain(generic, "SYSDATE");
    }

    [TestMethod]
    public void Hover_answers_in_the_dialect_that_was_asked() {
        var oracle = Languages().ById("oraclesql").Services
            .HoverAsync("SELECT NVL(a, b) FROM DUAL", 8).GetAwaiter().GetResult();
        StringAssert.Contains(oracle.Markdown, "NVL");

        var tsql = Languages().ById("sql").Services
            .HoverAsync("SELECT ISNULL(a, b)", 8).GetAwaiter().GetResult();
        StringAssert.Contains(tsql.Markdown, "ISNULL");

        // And says nothing about a word that is not in its dialect, rather than
        // inventing an explanation for it.
        Assert.IsNull(Languages().ById("sql").Services
            .HoverAsync("SELECT NVL(a, b)", 8).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void Only_t_sql_gets_t_sql_syntax_errors() {
        // `SELECT * FROM DUAL WHERE ROWNUM <= 1` is valid Oracle and the T-SQL
        // parser has no opinion worth trusting about it. What must not happen is
        // an Oracle cell being marked up by a parser for a different language.
        const string oracleSql = "SELECT * FROM emp WHERE ROWNUM <= 1 AND x = NVL(y, 0)";
        Assert.AreEqual(0, Languages().ById("oraclesql").Services.Diagnose(oracleSql).Count,
            "no Oracle parser here, so no Oracle syntax errors — and none borrowed from T-SQL");

        // T-SQL keeps the checker it has always had.
        Assert.IsTrue(Languages().ById("sql").Services.Diagnose("SELECT FROM WHERE").Count > 0,
            "T-SQL still parses");
    }

    // --- the dialect/provider join -------------------------------------------

    [TestMethod]
    public void A_dialect_says_which_providers_can_carry_it() {
        var languages = Languages();
        var tsql = (SqlDialectLanguage)languages.ById("sql");
        var oracle = (SqlDialectLanguage)languages.ById("oraclesql");

        Assert.IsTrue(tsql.Supports("SqlServer"));
        Assert.IsTrue(tsql.Supports("sqlserver"), "a $type is matched without regard to case");
        Assert.IsFalse(tsql.Supports("Oracle"));
        Assert.IsTrue(oracle.Supports("Oracle"));
        Assert.IsFalse(oracle.Supports("SqlServer"));

        // Both reach the same database through a driver somebody else installed.
        Assert.IsTrue(tsql.Supports("Odbc"));
        Assert.IsTrue(oracle.Supports("Odbc"));

        // An unknown type is not a yes. A provider nobody declared support for is
        // not one this dialect has been shown to work on.
        Assert.IsFalse(tsql.Supports("Postgres"));
        Assert.IsFalse(tsql.Supports(null));
    }

    [TestMethod]
    public async Task An_incompatible_pairing_is_refused_in_those_terms() {
        var engine = NewEngine();
        await engine.ExecuteAsync(
            "#!sql-connect --name warehouse --server localhost --database dw --auth integrated");

        var refusal = await Raised(engine, "#!oraclesql warehouse\nSELECT * FROM DUAL");

        Assert.IsNotNull(refusal, "Oracle SQL on a SQL Server connection cannot run");
        StringAssert.Contains(refusal.Message, "Oracle SQL");
        StringAssert.Contains(refusal.Message, "warehouse");
        StringAssert.Contains(refusal.Message, "SqlServer");
        StringAssert.Contains(refusal.Message, "Oracle, Odbc, Jdbc",
            "and says what would work, rather than only what does not");
    }

    [TestMethod]
    public async Task The_dialects_share_the_notebook_s_connections() {
        // A connection belongs to the notebook, not to the dialect that declared
        // it: registered once here, the generic dialect resolves the same name and
        // gets far enough to refuse it on provider grounds rather than on "no such
        // connection", which is what a per-dialect session would have said.
        var engine = NewEngine();
        await engine.ExecuteAsync(
            "#!sql-connect --name warehouse --server localhost --database dw --auth integrated");

        var refusal = await Raised(engine, "#!ansisql warehouse\nSELECT 1");

        StringAssert.Contains(refusal?.Message ?? "", "SqlServer");
        Assert.IsFalse((refusal?.Message ?? "").Contains("No SQL connection named"),
            "the name resolved; it was the pairing that did not");
    }

    [TestMethod]
    public void One_session_per_notebook_and_never_two_notebooks_on_one() {
        var first = Languages();
        var shared = first.Languages.OfType<SqlDialectLanguage>().Select(l => l.Session).ToList();
        Assert.AreEqual(3, shared.Count, "three dialects");
        Assert.AreEqual(1, shared.Distinct().Count(), "sharing one session");

        // And the other half of that rule, which is the one a shared static would
        // break: a second engine gets its own, or one notebook's connections would
        // show up in the next.
        var second = Languages();
        Assert.AreNotSame(
            shared[0], second.Languages.OfType<SqlDialectLanguage>().First().Session,
            "a second engine gets a session of its own");
    }
}
