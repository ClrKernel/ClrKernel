using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The early-warning scan over a statement.
/// <para>
/// The asymmetry is the whole design and these tests hold it: a false positive is a
/// bug somebody cannot work around, so reads must never be refused; a false negative
/// is a message they do not get before the database says no anyway, so misses are
/// expected and several are asserted deliberately.
/// </para>
/// </summary>
[TestClass]
public class StatementCheckTest {
    [TestMethod]
    public void APlainSelectIsARead() {
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT * FROM dbo.Orders"));
        Assert.IsNull(StatementCheck.WriteVerbIn("  select 1  "));
        Assert.IsNull(StatementCheck.WriteVerbIn("(SELECT 1)"));
    }

    [TestMethod]
    public void AWriteIsNamedByItsVerb() {
        Assert.AreEqual("INSERT", StatementCheck.WriteVerbIn("INSERT INTO dbo.Orders VALUES (1)"));
        Assert.AreEqual("UPDATE", StatementCheck.WriteVerbIn("update dbo.Orders set Total = 1"));
        Assert.AreEqual("DELETE", StatementCheck.WriteVerbIn("DELETE FROM dbo.Orders"));
        Assert.AreEqual("DROP", StatementCheck.WriteVerbIn("drop table dbo.Orders"));
        Assert.AreEqual("EXEC", StatementCheck.WriteVerbIn("EXEC dbo.DoSomething"));
        Assert.AreEqual("TRUNCATE", StatementCheck.WriteVerbIn("TRUNCATE TABLE dbo.Orders"));
    }

    [TestMethod]
    public void AWriteAnywhereInABatchIsFound() {
        Assert.AreEqual("DELETE",
            StatementCheck.WriteVerbIn("SELECT 1;\nDELETE FROM dbo.Orders;\nSELECT 2"));
    }

    [TestMethod]
    public void AndAcrossABatchSeparator() {
        Assert.AreEqual("UPDATE",
            StatementCheck.WriteVerbIn("SELECT 1\nGO\nUPDATE dbo.Orders SET Total = 0"));
    }

    // --- what must never be refused -----------------------------------------

    [TestMethod]
    public void AWordInsideAStringIsNotAVerb() {
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT 'delete me' AS note"));
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT * FROM dbo.T WHERE Note = 'drop table'"));
    }

    [TestMethod]
    public void NorIsOneInAComment() {
        Assert.IsNull(StatementCheck.WriteVerbIn("-- delete this later\nSELECT 1"));
        Assert.IsNull(StatementCheck.WriteVerbIn("/* update the report */ SELECT 1"));
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT 1 -- drop"));
    }

    [TestMethod]
    public void NorAColumnThatHappensToBeCalledOne() {
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT [Delete] FROM dbo.Permissions"));
    }

    [TestMethod]
    public void NorAVerbThatIsNotTheFirstWord() {
        // The row it selects is about an update; the statement is a read.
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT UpdatedAt FROM dbo.Orders"));
    }

    [TestMethod]
    public void AnUnterminatedStringDoesNotSwallowTheWholeStatement() {
        // A typo mid-edit must not throw, and must not start reporting verbs from
        // inside what is now one long string.
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT 'oops"));
    }

    [TestMethod]
    public void NothingAtAllIsARead() {
        Assert.IsNull(StatementCheck.WriteVerbIn(null));
        Assert.IsNull(StatementCheck.WriteVerbIn(""));
        Assert.IsNull(StatementCheck.WriteVerbIn("   \n  "));
    }

    // --- what it deliberately misses ----------------------------------------

    [TestMethod]
    public void ACteEndingInAWriteIsNotCaught() {
        // Asserted, not lamented: this is the case the spec names when it says not to
        // enforce read-only by parsing SQL. The read-only login refuses it.
        Assert.IsNull(StatementCheck.WriteVerbIn(
            "WITH x AS (SELECT 1 AS n) INSERT INTO dbo.T SELECT n FROM x"));
    }

    [TestMethod]
    public void NorIsASelectInto() {
        // SELECT … INTO creates a table, and it starts with SELECT.
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT * INTO dbo.Copy FROM dbo.Orders"));
    }

    [TestMethod]
    public void NorIsDynamicSql() {
        Assert.IsNull(StatementCheck.WriteVerbIn("SELECT 1; -- sp_executesql lives here"));
    }

    [TestMethod]
    public void TheRefusalSaysWhereTheBoundaryActuallyIs() {
        var message = StatementCheck.Refusal("DELETE");
        StringAssert.Contains(message, "DELETE");
        StringAssert.Contains(message, "read-only login");
        StringAssert.Contains(message, "The database is what enforces that");
    }
}
