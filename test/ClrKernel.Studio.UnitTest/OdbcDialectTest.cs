using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Which limit the ODBC dialect writes for "Select Top 1000". ANSI by default; the
/// engine's own form only where the standard one does not run. The live half is
/// <see cref="OdbcLiveTest.TheGeneratedSelectRuns"/>, which CI points at PostgreSQL.
/// </summary>
[TestClass]
public class OdbcDialectTest {
    [TestMethod]
    public void An_unknown_or_standard_engine_gets_ANSI_fetch_first() {
        StringAssert.Contains(OdbcDialect.LimitedSelect("*", "t", null), "FETCH FIRST 1000 ROWS ONLY");
        StringAssert.Contains(OdbcDialect.LimitedSelect("*", "t", "PostgreSQL"), "FETCH FIRST 1000 ROWS ONLY");
        StringAssert.Contains(OdbcDialect.LimitedSelect("*", "t", "Oracle"), "FETCH FIRST 1000 ROWS ONLY");
    }

    [TestMethod]
    public void SQL_Server_gets_TOP_because_it_has_no_bare_FETCH_FIRST() {
        var sql = OdbcDialect.LimitedSelect("a, b", "t", "Microsoft SQL Server");
        StringAssert.StartsWith(sql, "SELECT TOP 1000 a, b");
        Assert.IsFalse(sql.Contains("FETCH"), sql);
    }

    [TestMethod]
    public void MySQL_MariaDB_and_SQLite_get_LIMIT() {
        foreach (var engine in new[] { "MySQL", "MariaDB", "SQLite" }) {
            var sql = OdbcDialect.LimitedSelect("*", "t", engine);
            StringAssert.Contains(sql, "LIMIT 1000", engine);
            Assert.IsFalse(sql.Contains("FETCH"), engine);
        }
    }
}
