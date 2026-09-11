using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Database;
using ClrKernel.Database.Provider.Fabric;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parquet;

namespace ClrKernel.UnitTest;

[TestClass]
public class FabricTableDefinitionTest {
    [TestMethod]
    public void FabricType_maps_clr_types_to_fabric_supported_types() {
        Assert.AreEqual("bit", WarehouseTableDefinition.FabricType(typeof(bool), -1, -1, -1));
        Assert.AreEqual("int", WarehouseTableDefinition.FabricType(typeof(int), -1, -1, -1));
        Assert.AreEqual("bigint", WarehouseTableDefinition.FabricType(typeof(long), -1, -1, -1));
        Assert.AreEqual("float", WarehouseTableDefinition.FabricType(typeof(double), -1, -1, -1));
        Assert.AreEqual("datetime2(3)", WarehouseTableDefinition.FabricType(typeof(DateTime), -1, -1, -1));
        Assert.AreEqual("uniqueidentifier", WarehouseTableDefinition.FabricType(typeof(Guid), -1, -1, -1));
        Assert.AreEqual("decimal(18,4)", WarehouseTableDefinition.FabricType(typeof(decimal), -1, 18, 4));
    }

    [TestMethod]
    public void FabricType_strings_use_utf8_varchar_never_nvarchar() {
        var big = WarehouseTableDefinition.FabricType(typeof(string), -1, -1, -1);
        StringAssert.Contains(big, "varchar(max)");
        StringAssert.Contains(big, WarehouseTableDefinition.Utf8Collation);
        Assert.IsFalse(big.Contains("nvarchar"), "Fabric Warehouse does not support nvarchar");

        var sized = WarehouseTableDefinition.FabricType(typeof(string), 100, -1, -1);
        StringAssert.Contains(sized, "varchar(100)");
    }

    [TestMethod]
    public void ToFabricTypes_rewrites_unsupported_types() {
        var fixedUp = WarehouseTableDefinition.ToFabricTypes(
            "CREATE TABLE t (a nvarchar(max), b datetime)");
        StringAssert.Contains(fixedUp, "varchar(max) collate " + WarehouseTableDefinition.Utf8Collation);
        StringAssert.Contains(fixedUp, "datetime2(3)");
        Assert.IsFalse(fixedUp.Contains("nvarchar"));
    }

    [TestMethod]
    public void Generate_builds_create_table_from_reader_schema() {
        using var reader = SampleTable().CreateDataReader();
        var ddl = WarehouseTableDefinition.Generate(reader, "dbo.FactSales");
        StringAssert.StartsWith(ddl, "CREATE TABLE [dbo].[FactSales] (");
        StringAssert.Contains(ddl, "[Id] int");
        StringAssert.Contains(ddl, "[Amount] decimal");
        StringAssert.Contains(ddl, "varchar"); // Name column
        Assert.IsFalse(ddl.Contains("nvarchar"));
    }

    private static DataTable SampleTable() {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("When", typeof(DateTime));
        t.Columns.Add("Amount", typeof(decimal));
        t.Rows.Add(1, "alpha", new DateTime(2024, 1, 1), 12.50m);
        t.Rows.Add(2, "beta", new DateTime(2024, 2, 2), 7.00m);
        return t;
    }

    internal static DataTable Sample() => SampleTable();
}

[TestClass]
public class FabricParquetTest {
    [TestMethod]
    public async Task WriteAsync_streams_reader_and_reports_row_count() {
        using var reader = FabricTableDefinitionTest.Sample().CreateDataReader();
        using var stream = new MemoryStream();
        var result = await FabricParquet.WriteAsync(reader, stream, rowGroupSize: 1);

        Assert.AreEqual(2, result.RowCount);
        CollectionAssert.AreEqual(new[] { "Id", "Name", "When", "Amount" }, result.Columns);

        stream.Position = 0;
        using var pq = await ParquetReader.CreateAsync(stream);
        CollectionAssert.AreEquivalent(
            new[] { "Id", "Name", "When", "Amount" },
            pq.Schema.Fields.Select(f => f.Name).ToArray());
    }

    [TestMethod]
    public async Task WriteAsync_reader_can_only_be_consumed_once() {
        using var reader = FabricTableDefinitionTest.Sample().CreateDataReader();
        using var s1 = new MemoryStream();
        await FabricParquet.WriteAsync(reader, s1);
        // Reader is exhausted; a second pass writes zero rows (does not throw).
        using var s2 = new MemoryStream();
        var second = await FabricParquet.WriteAsync(reader, s2);
        Assert.AreEqual(0, second.RowCount);
    }
}

[TestClass]
public class FabricReloadRequestTest {
    [TestMethod]
    public void Target_and_label_default_sensibly() {
        var r = new FabricReloadRequest { TableName = "FactSales" };
        Assert.AreEqual("[dbo].[FactSales]", r.Target);
        Assert.AreEqual("[dbo].[FactSales]", r.Label);

        var named = new FabricReloadRequest { TableSchema = "stg", TableName = "T", SegmentName = "2024-Q1" };
        Assert.AreEqual("[stg].[T]", named.Target);
        Assert.AreEqual("2024-Q1", named.Label);
    }

    /// <summary>A table name is one identifier: the dots in it stay inside its brackets.</summary>
    [TestMethod]
    public void A_dotted_table_name_is_one_identifier() {
        var r = new FabricReloadRequest("Mart", "COMPANY.Dimension.Forecast");
        Assert.AreEqual("[Mart].[COMPANY.Dimension.Forecast]", r.Target);
        Assert.AreEqual("select * from [Mart].[COMPANY.Dimension.Forecast]", r.EffectiveSource);
        Assert.AreEqual("TRUNCATE TABLE [Mart].[COMPANY.Dimension.Forecast]", r.EffectiveDelete());

        var explicitSource = new FabricReloadRequest("Mart", "T", sourceQuery: "select * from [db].[Mart].[T]", segmentFilter: "Year = 2026");
        Assert.AreEqual("select * from [db].[Mart].[T]", explicitSource.EffectiveSource);
        Assert.AreEqual("DELETE FROM [Mart].[T] WHERE Year = 2026", explicitSource.EffectiveDelete());
    }

    [TestMethod]
    public void EffectiveDelete_prefers_command_then_filter_then_truncate() {
        Assert.AreEqual("DELETE FROM x",
            new FabricReloadRequest { TableName = "T", DeleteCommand = "DELETE FROM x", SegmentFilter = "Year=2024" }.EffectiveDelete());

        var byFilter = new FabricReloadRequest { TableName = "T", SegmentFilter = "Year = 2024" }.EffectiveDelete();
        Assert.AreEqual("DELETE FROM [dbo].[T] WHERE Year = 2024", byFilter);

        // A reload with nothing to say about the segment is a full reload, not an append.
        Assert.AreEqual("TRUNCATE TABLE [dbo].[T]", new FabricReloadRequest { TableName = "T" }.EffectiveDelete());
    }

    [TestMethod]
    public void Validate_requires_table_name() {
        Assert.ThrowsExactly<InvalidOperationException>(() => new FabricReloadRequest().Validate());
    }

    [TestMethod]
    public void A_whole_table_copy_selects_from_the_quoted_name() {
        var source = new DataSource("src", () => throw new InvalidOperationException("never opened"));
        Assert.AreEqual("select * from [Mart].[COMPANY.Dimension.Forecast]",
            FabricWarehouse.SelectAll(source, "Mart.[COMPANY.Dimension.Forecast]").Sql);
    }
}

[TestClass]
public class FabricEngineWiringTest {
    [TestMethod]
    public async Task Fabric_helper_is_referenced_and_imported_in_csharp_cells() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        // Bare "Fabric" resolves to the imported helper type (no ClrKernel.Database.Provider.Fabric prefix needed).
        var result = await engine.ExecuteAsync("#!csharp\nnameof(Fabric)");
        var text = result is ClrKernel.Core.Scripting.DisplayData d && d.Data.TryGetValue("text/plain", out var t)
            ? t?.ToString()
            : result?.ToString();
        StringAssert.Contains(text, "Fabric");
    }
}

[TestClass]
public class FabricFactoryTest {
    [TestMethod]
    public void Interactive_uses_the_browser_only_credential() {
        // Constructing the credential never prompts; only a token request would.
        var connection = ClrKernel.Database.Provider.Fabric.Fabric.Interactive();
        Assert.IsNotNull(connection);
    }

    [TestMethod]
    public void WithCredential_rejects_null() {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => ClrKernel.Database.Provider.Fabric.Fabric.WithCredential(null));
    }

    [TestMethod]
    public void InteractiveOnly_is_a_bare_browser_credential_not_a_chain() {
        Assert.IsInstanceOfType(
            ClrKernel.Database.Entra.EntraAuth.InteractiveOnly(),
            typeof(Azure.Identity.InteractiveBrowserCredential),
            "no chain: the browser must always be the one asked");
    }
}

/// <summary>
/// The staging file is addressed by id, and the id form of a OneLake path has no
/// ".Lakehouse" suffix — that belongs to the friendly-name form. With the suffix on
/// a Guid, OneLake refused the upload with FriendlyNameSupportDisabled (400) on a
/// real workspace.
/// </summary>
[TestClass]
public class FabricLakehousePathTest {
    [TestMethod]
    public void Staging_paths_use_ids_without_the_Lakehouse_suffix() {
        var workspaceId = Guid.NewGuid();
        var lakehouseId = Guid.NewGuid();
        var lakehouse = new FabricLakehouse(new FabricWorkspace(null, workspaceId, "Analytics"), lakehouseId, "Lakehouse_Staging");

        Assert.AreEqual($"{lakehouseId}/Files/Staging-BulkInsert/x.parquet", lakehouse.FilesPath("/Staging-BulkInsert\\x.parquet"));
        Assert.AreEqual(
            $"https://onelake.dfs.fabric.microsoft.com/{workspaceId}/{lakehouseId}/Files/Staging-BulkInsert/x.parquet",
            lakehouse.OneLakeUrl("Staging-BulkInsert/x.parquet"));
    }
}
