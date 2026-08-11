using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlIdentifierTest {
    [TestMethod]
    public void Quotes_plain_and_dotted_names() {
        Assert.AreEqual("[Orders]", SqlIdentifier.Quote("Orders"));
        Assert.AreEqual("[dbo].[Orders]", SqlIdentifier.Quote("dbo.Orders"));
        Assert.AreEqual("[dbo].[Orders]", SqlIdentifier.Quote("[dbo].[Orders]"));
    }

    [TestMethod]
    public void Escapes_closing_bracket_to_prevent_injection() {
        Assert.AreEqual("[a]]; DROP TABLE x --]", SqlIdentifier.Quote("a]; DROP TABLE x --"));
    }
}

[TestClass]
public class DataTableBuilderTest {
    [TestMethod]
    public void Scalar_array_becomes_single_column() {
        var table = DataTableBuilder.FromRows(new[] { 1, 2, 3 });
        Assert.AreEqual(1, table.Columns.Count);
        Assert.AreEqual("Value", table.Columns[0].ColumnName);
        Assert.AreEqual(typeof(int), table.Columns[0].DataType);
        Assert.AreEqual(3, table.Rows.Count);
    }

    [TestMethod]
    public void Poco_becomes_column_per_property() {
        var rows = new[] { new { Id = 1, Name = "a" }, new { Id = 2, Name = "b" } };
        var table = DataTableBuilder.FromRows(rows);
        Assert.AreEqual(2, table.Columns.Count);
        CollectionAssert.AreEquivalent(new[] { "Id", "Name" },
            table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.AreEqual(2, table.Rows.Count);
        Assert.AreEqual("a", table.Rows[0]["Name"]);
    }

    [TestMethod]
    public void Dictionaries_union_keys_and_fill_missing_with_dbnull() {
        var rows = new List<IDictionary<string, object>> {
            new Dictionary<string, object> { ["Id"] = 1, ["Name"] = "a" },
            new Dictionary<string, object> { ["Id"] = 2 },
        };
        var table = DataTableBuilder.FromDictionaries(rows);
        Assert.AreEqual(2, table.Columns.Count);
        Assert.AreEqual(DBNull.Value, table.Rows[1]["Name"]);
    }

    [TestMethod]
    public void Nullable_scalars_use_underlying_type() {
        var table = DataTableBuilder.FromRows(new int?[] { 1, null, 3 });
        Assert.AreEqual(typeof(int), table.Columns[0].DataType);
        Assert.AreEqual(DBNull.Value, table.Rows[1][0]);
    }
}

[TestClass]
public class MergeBuilderTest {
    private static MergeSpec BasicSpec() => new MergeSpec {
        Target = "dbo.Customers",
        Source = "stg.Customers",
        KeyColumns = new List<string> { "Id" },
        UpdateColumns = new List<string> { "Name", "Email" },
    };

    [TestMethod]
    public void Generates_valid_tsql() {
        var sql = MergeBuilder.Build(BasicSpec());
        Assert.IsTrue(TSqlSyntax.IsValid(sql), "generated MERGE should be valid T-SQL:\n" + sql);
    }

    [TestMethod]
    public void Includes_matched_insert_and_counts() {
        var sql = MergeBuilder.Build(BasicSpec());
        StringAssert.Contains(sql, "MERGE [dbo].[Customers] AS T");
        StringAssert.Contains(sql, "USING [stg].[Customers] AS S");
        StringAssert.Contains(sql, "WHEN MATCHED THEN UPDATE SET T.[Name] = S.[Name], T.[Email] = S.[Email]");
        StringAssert.Contains(sql, "WHEN NOT MATCHED BY TARGET THEN INSERT ([Id], [Name], [Email])");
        StringAssert.Contains(sql, "OUTPUT $action INTO @clr_actions");
    }

    [TestMethod]
    public void Delete_not_matched_by_source_is_optional_and_valid() {
        var spec = BasicSpec();
        spec.DeleteNotMatchedBySource = true;
        var sql = MergeBuilder.Build(spec);
        StringAssert.Contains(sql, "WHEN NOT MATCHED BY SOURCE THEN DELETE");
        Assert.IsTrue(TSqlSyntax.IsValid(sql));
    }

    [TestMethod]
    public void Omits_matched_clause_when_no_update_columns() {
        var spec = BasicSpec();
        spec.UpdateColumns = new List<string>(); // keys only
        var sql = MergeBuilder.Build(spec);
        Assert.IsFalse(sql.Contains("WHEN MATCHED"), "no update columns → no WHEN MATCHED clause");
        Assert.IsTrue(TSqlSyntax.IsValid(sql));
    }

    [TestMethod]
    public void Query_source_is_wrapped_and_valid() {
        var spec = BasicSpec();
        spec.Source = "SELECT Id, Name, Email FROM stg.Raw WHERE Active = 1";
        var sql = MergeBuilder.Build(spec);
        StringAssert.Contains(sql, "USING (SELECT Id, Name, Email FROM stg.Raw WHERE Active = 1) AS S");
        Assert.IsTrue(TSqlSyntax.IsValid(sql));
    }

    [TestMethod]
    public void Requires_keys() {
        var spec = BasicSpec();
        spec.KeyColumns = new List<string>();
        Assert.ThrowsExactly<ArgumentException>(() => MergeBuilder.Build(spec));
    }
}
