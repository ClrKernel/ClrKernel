using System;
using System.Data;
using System.Linq;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.DataEngineering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The table-action model is data, so all of it is testable without a database. These cover the
/// two halves that can be: that an action can't be built in a shape its kind doesn't allow, and
/// that SQL Server's translation of an action into T-SQL is what we think it is. Executing the
/// resulting statements needs a server and is on the Windows checklist.
/// </summary>
[TestClass]
public class TableActionTest {
    private static TableSource Source => TableSource.Query("staging", "select * from dbo.Raw");

    [TestMethod]
    public void Actions_that_load_rows_require_a_source() {
        Assert.ThrowsExactly<ArgumentNullException>(() => TableAction.Insert("dbo.T", null));
        Assert.ThrowsExactly<ArgumentNullException>(() => TableAction.TruncateInsert("dbo.T", null));
        Assert.ThrowsExactly<ArgumentNullException>(() => TableAction.DeleteInsert("dbo.T", null));

        // ...and the ones that only remove rows don't take one.
        Assert.IsNull(TableAction.Delete("dbo.T").Source);
        Assert.IsNull(TableAction.Truncate("dbo.T").Source);
    }

    [TestMethod]
    public void Merge_requires_at_least_one_key_column() {
        Assert.ThrowsExactly<ArgumentException>(() => TableAction.Merge("dbo.T", Source, null));
        Assert.ThrowsExactly<ArgumentException>(() => TableAction.Merge("dbo.T", Source, new[] { " " }));
        CollectionAssert.AreEqual(new[] { "Id" }, TableAction.Merge("dbo.T", Source, new[] { " Id " }).KeyColumns.ToArray());
    }

    [TestMethod]
    public void Every_action_needs_a_target_table() {
        Assert.ThrowsExactly<ArgumentException>(() => TableAction.Truncate(" "));
        Assert.ThrowsExactly<ArgumentException>(() => TableAction.Insert(null, Source));
    }

    [TestMethod]
    public void A_blank_predicate_means_every_row_not_an_empty_where() {
        Assert.IsNull(TableAction.Delete("dbo.T", "   ").Where, "blank should normalise to 'no predicate'");
        Assert.AreEqual("Year = 2026", TableAction.Delete("dbo.T", "  Year = 2026 ").Where);
    }

    [TestMethod]
    public void DeletesRows_identifies_the_destructive_kinds() {
        Assert.IsTrue(TableAction.Delete("t").DeletesRows);
        Assert.IsTrue(TableAction.Truncate("t").DeletesRows);
        Assert.IsTrue(TableAction.TruncateInsert("t", Source).DeletesRows);
        Assert.IsTrue(TableAction.DeleteInsert("t", Source).DeletesRows);
        Assert.IsFalse(TableAction.Insert("t", Source).DeletesRows);
        Assert.IsFalse(TableAction.Merge("t", Source, new[] { "Id" }).DeletesRows);
    }

    [TestMethod]
    public void A_query_source_has_no_client_reader() {
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => Source.OpenReader());
        StringAssert.Contains(e.Message, "server");
    }

    [TestMethod]
    public void Rows_source_opens_its_reader_once_per_execution() {
        var opened = 0;
        var source = TableSource.Rows(() => { opened++; return new DataTable().CreateDataReader(); });
        source.OpenReader().Dispose();
        source.OpenReader().Dispose();
        Assert.AreEqual(2, opened, "the factory is per-execution so an action can be retried");
    }

    // ---- SQL Server translation ------------------------------------------------

    [TestMethod]
    public void Truncating_kinds_emit_truncate_and_deleting_kinds_emit_delete() {
        Assert.AreEqual("truncate table dbo.T", SqlServerTableActions.DeleteStatement(TableAction.Truncate("dbo.T")));
        Assert.AreEqual("truncate table dbo.T",
            SqlServerTableActions.DeleteStatement(TableAction.TruncateInsert("dbo.T", Source)));
        Assert.AreEqual("delete from dbo.T", SqlServerTableActions.DeleteStatement(TableAction.Delete("dbo.T")));
        Assert.AreEqual("delete from dbo.T where Year = 2026",
            SqlServerTableActions.DeleteStatement(TableAction.Delete("dbo.T", "Year = 2026")));
    }

    [TestMethod]
    public void An_unscoped_DeleteInsert_stays_a_delete_rather_than_becoming_a_truncate() {
        // They differ in logging, identity reseed and triggers, so swapping them would be a
        // behaviour change the caller never asked for.
        Assert.AreEqual("delete from dbo.T",
            SqlServerTableActions.DeleteStatement(TableAction.DeleteInsert("dbo.T", Source)));
    }

    [TestMethod]
    public void Load_only_actions_have_no_delete_statement() {
        Assert.IsNull(SqlServerTableActions.DeleteStatement(TableAction.Insert("dbo.T", Source)));
        Assert.IsNull(SqlServerTableActions.DeleteStatement(TableAction.Merge("dbo.T", Source, new[] { "Id" })));
    }

    [TestMethod]
    public void A_table_source_reads_through_select_star() {
        Assert.AreEqual("select * from dbo.Raw", SqlServerTableActions.SourceQuery(TableSource.Table("c", "dbo.Raw")));
        Assert.AreEqual("select 1", SqlServerTableActions.SourceQuery(TableSource.Query("c", "select 1")));
    }

    [TestMethod]
    public void Merge_maps_onto_a_MergeSpec_without_double_wrapping_the_source() {
        var spec = SqlServerTableActions.ToMergeSpec(
            TableAction.Merge("dbo.Customers", TableSource.Query("wh", "select * from stg.Raw"), new[] { "Id" }));

        Assert.AreEqual("dbo.Customers", spec.Target);
        // Raw, not "(...)" — MergeBuilder parenthesises a query itself.
        Assert.AreEqual("select * from stg.Raw", spec.Source);
        Assert.AreEqual(true, spec.SourceIsQuery);
        CollectionAssert.AreEqual(new[] { "Id" }, spec.KeyColumns.ToArray());

        spec.UpdateColumns = new[] { "Name" };
        StringAssert.Contains(MergeBuilder.Build(spec), "USING (select * from stg.Raw) AS S");
    }

    [TestMethod]
    public void Merging_from_in_memory_rows_is_refused_with_the_workaround() {
        var action = TableAction.Merge("dbo.T", TableSource.Rows(() => new DataTable().CreateDataReader()), new[] { "Id" });
        var e = Assert.ThrowsExactly<NotSupportedException>(() => SqlServerTableActions.ToMergeSpec(action));
        StringAssert.Contains(e.Message, "staging table");
    }
}

/// <summary>
/// The merge-source guard runs before any connection is opened, so it is testable offline —
/// and it needs to be: it decides whether a legitimate call is refused.
/// </summary>
[TestClass]
public class SqlServerMergeSourceGuardTest {
    private static SqlConnectionRegistry Registry() {
        var registry = new SqlConnectionRegistry();
        registry.Register(new SqlConnectionSpec { Name = "warehouse", Server = "s1", Database = "d" }, asDefault: true);
        registry.Register(new SqlConnectionSpec { Name = "staging", Server = "s2", Database = "d" }, asDefault: false);
        return registry;
    }

    private static TableAction MergeFrom(string connection) =>
        TableAction.Merge("dbo.T", TableSource.Query(connection, "select 1"), new[] { "Id" });

    [TestMethod]
    public void Naming_the_default_connection_explicitly_is_still_the_target() {
        // Target left as null (the registry default = "warehouse"); the source names it outright.
        // Comparing raw strings would refuse this and send the caller off to stage a table that is
        // already on the right server.
        var target = new SqlServerTableTarget(Registry());
        target.MergeSourceMustBeOnTarget(MergeFrom("warehouse"));
    }

    [TestMethod]
    public void An_omitted_source_connection_means_the_target() {
        new SqlServerTableTarget(Registry()).MergeSourceMustBeOnTarget(MergeFrom(null));
    }

    [TestMethod]
    public void A_genuinely_different_connection_is_refused_with_both_names() {
        var target = new SqlServerTableTarget(Registry(), connection: "warehouse");
        var e = Assert.ThrowsExactly<NotSupportedException>(() => target.MergeSourceMustBeOnTarget(MergeFrom("staging")));
        StringAssert.Contains(e.Message, "'staging' is not 'warehouse'");
        StringAssert.Contains(e.Message, "staging table");
    }
}
