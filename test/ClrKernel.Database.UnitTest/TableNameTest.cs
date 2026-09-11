using System;
using ClrKernel.Database;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Bracket-aware, the way T-SQL reads a name. The old split-on-dot turned
/// <c>[Mart].[COMPANY.Dimension.Forecast]</c> into four parts and a CREATE TABLE
/// nothing could find again.
/// </summary>
[TestClass]
public class TableNameTest {
    [TestMethod]
    public void Brackets_make_a_part_atomic() {
        CollectionAssert.AreEqual(new[] { "Mart", "COMPANY.Dimension.Forecast" },
            (System.Collections.ICollection)TableName.Parts("[Mart].[COMPANY.Dimension.Forecast]"));
        CollectionAssert.AreEqual(new[] { "Mart", "COMPANY.Dimension.Forecast" },
            (System.Collections.ICollection)TableName.Parts("Mart.[COMPANY.Dimension.Forecast]"));
        CollectionAssert.AreEqual(new[] { "dbo", "T" }, (System.Collections.ICollection)TableName.Parts("dbo.T"));
        CollectionAssert.AreEqual(new[] { "db", "dbo", "T" }, (System.Collections.ICollection)TableName.Parts("db.dbo.T"));
    }

    [TestMethod]
    public void Quote_brackets_every_part_and_doubles_a_closing_bracket() {
        Assert.AreEqual("[dbo].[T]", TableName.Quote("dbo.T"));
        Assert.AreEqual("[dbo].[T]", TableName.Quote("[dbo].[T]"), "quoting is idempotent");
        Assert.AreEqual("[Mart].[COMPANY.Dimension.Forecast]", TableName.Quote("[Mart].[COMPANY.Dimension.Forecast]"));
        Assert.AreEqual("[odd]]name]", TableName.Quote("[odd]]name]"));
        Assert.AreEqual("[odd]]name]", TableName.Quote("odd]name"));
    }

    [TestMethod]
    public void SchemaAndName_take_the_last_two_parts() {
        Assert.AreEqual(("Mart", "COMPANY.Dimension.Forecast"), TableName.SchemaAndName("[Mart].[COMPANY.Dimension.Forecast]"));
        Assert.AreEqual(("dbo", "T"), TableName.SchemaAndName("db.dbo.T"));
        Assert.AreEqual((null, "T"), TableName.SchemaAndName("T"));
        Assert.ThrowsExactly<ArgumentException>(() => TableName.Parts(" "));
    }
}
