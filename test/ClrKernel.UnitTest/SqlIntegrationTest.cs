using System;
using System.Collections.Generic;
using System.IO;
using ClrKernel.Sql;
using ClrKernel.Sql.Deploy;
using ClrKernel.Sql.Etl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// End-to-end bulk copy + MERGE against a real SQL Server. Skipped unless the
/// CLRKERNEL_TEST_SQL environment variable holds a connection string, so it does
/// not run in CI without a server. Point it at any SQL Server (e.g. a local
/// Docker container) to validate the ETL path against live SqlBulkCopy / MERGE.
/// </summary>
[TestClass]
public class SqlIntegrationTest {
    private static string ConnectionString => Environment.GetEnvironmentVariable("CLRKERNEL_TEST_SQL");

    private static SqlSession NewSession() {
        var session = new SqlSession();
        session.Connect($"#!sql-connect --name it --connection-string \"{ConnectionString}\"");
        return session;
    }

    [TestInitialize]
    public void RequireServer() {
        if (string.IsNullOrWhiteSpace(ConnectionString)) {
            Assert.Inconclusive("Set CLRKERNEL_TEST_SQL to run SQL integration tests.");
        }
    }

    private static void Exec(SqlSession session, string sql) {
        using var conn = session.OpenConnection("it");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqlSession session, string sql) {
        using var conn = session.OpenConnection("it");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [TestMethod]
    public void BulkCopy_then_merge_upserts_correctly() {
        var session = NewSession();
        Exec(session, "IF OBJECT_ID('dbo.ClrTarget') IS NOT NULL DROP TABLE dbo.ClrTarget;");
        Exec(session, "IF OBJECT_ID('dbo.ClrStg') IS NOT NULL DROP TABLE dbo.ClrStg;");
        Exec(session, "CREATE TABLE dbo.ClrTarget (Id INT PRIMARY KEY, Name NVARCHAR(50), Amount DECIMAL(18,2));");
        Exec(session, "CREATE TABLE dbo.ClrStg (Id INT PRIMARY KEY, Name NVARCHAR(50), Amount DECIMAL(18,2));");

        // Bulk copy 3 rows from an in-memory collection ("array variables").
        var rows = new[] {
            new { Id = 1, Name = "Ann", Amount = 10.5m },
            new { Id = 2, Name = "Ben", Amount = 20.0m },
            new { Id = 3, Name = "Cy", Amount = 30.0m },
        };
        var bulk = session.BulkCopy("it", "dbo.ClrTarget", rows, new BulkCopyOptions { ShowProgress = false });
        Assert.AreEqual(3, bulk.RowsCopied);
        Assert.AreEqual(3, Scalar(session, "SELECT COUNT(*) FROM dbo.ClrTarget;"));

        // Staging: update Id 2, keep Id 3 same-ish, insert Id 4.
        var stg = new[] {
            new { Id = 2, Name = "Ben2", Amount = 25.0m },
            new { Id = 3, Name = "Cy", Amount = 30.0m },
            new { Id = 4, Name = "Dee", Amount = 40.0m },
        };
        session.BulkCopy("it", "dbo.ClrStg", stg, new BulkCopyOptions { ShowProgress = false });

        // MERGE dbo.ClrStg into dbo.ClrTarget on Id (columns introspected).
        var result = session.Merge("it", new MergeSpec {
            Target = "dbo.ClrTarget",
            Source = "dbo.ClrStg",
            KeyColumns = new List<string> { "Id" },
        });

        // Id 4 inserted; Id 2 and Id 3 matched → updated (MERGE counts a match as an
        // update even when values are unchanged).
        Assert.AreEqual(1, result.Inserted, "one new row");
        Assert.AreEqual(2, result.Updated, "two matched rows");
        Assert.AreEqual(4, Scalar(session, "SELECT COUNT(*) FROM dbo.ClrTarget;"));
        Assert.AreEqual(1, Scalar(session, "SELECT COUNT(*) FROM dbo.ClrTarget WHERE Id = 2 AND Name = 'Ben2';"));

        Exec(session, "DROP TABLE dbo.ClrTarget; DROP TABLE dbo.ClrStg;");
    }

    [TestMethod]
    public void Merge_with_delete_removes_missing_rows() {
        var session = NewSession();
        Exec(session, "IF OBJECT_ID('dbo.ClrT2') IS NOT NULL DROP TABLE dbo.ClrT2;");
        Exec(session, "IF OBJECT_ID('dbo.ClrS2') IS NOT NULL DROP TABLE dbo.ClrS2;");
        Exec(session, "CREATE TABLE dbo.ClrT2 (Id INT PRIMARY KEY, Name NVARCHAR(50));");
        Exec(session, "CREATE TABLE dbo.ClrS2 (Id INT PRIMARY KEY, Name NVARCHAR(50));");
        Exec(session, "INSERT INTO dbo.ClrT2 VALUES (1,'a'),(2,'b'),(3,'c');");
        Exec(session, "INSERT INTO dbo.ClrS2 VALUES (1,'a'),(2,'B');");

        var result = session.Merge("it", new MergeSpec {
            Target = "dbo.ClrT2",
            Source = "dbo.ClrS2",
            KeyColumns = new List<string> { "Id" },
            DeleteNotMatchedBySource = true,
        });
        Assert.AreEqual(1, result.Deleted, "Id 3 is missing from source → deleted");
        Assert.AreEqual(2, Scalar(session, "SELECT COUNT(*) FROM dbo.ClrT2;"));

        Exec(session, "DROP TABLE dbo.ClrT2; DROP TABLE dbo.ClrS2;");
    }

    [TestMethod]
    public void Pipeline_runs_steps_in_dependency_order() {
        var session = NewSession();
        Exec(session, "IF OBJECT_ID('dbo.PipeA') IS NOT NULL DROP TABLE dbo.PipeA;");
        Exec(session, "IF OBJECT_ID('dbo.PipeB') IS NOT NULL DROP TABLE dbo.PipeB;");
        Exec(session, "IF OBJECT_ID('dbo.PipeTarget') IS NOT NULL DROP TABLE dbo.PipeTarget;");
        Exec(session, "CREATE TABLE dbo.PipeA (Id INT); CREATE TABLE dbo.PipeB (Id INT); CREATE TABLE dbo.PipeTarget (Id INT);");

        // Two independent loads and a combine step that depends on both.
        session.Execute("-- step load_a\n-- connections it\nINSERT INTO dbo.PipeA VALUES (1),(2);");
        session.Execute("-- step load_b\n-- connections it\nINSERT INTO dbo.PipeB VALUES (3),(4),(5);");
        session.Execute("-- step combine\n-- needs load_a, load_b\n-- connections it\n" +
                        "INSERT INTO dbo.PipeTarget SELECT Id FROM dbo.PipeA UNION ALL SELECT Id FROM dbo.PipeB;");

        session.ExecuteRun("#!sql-run");
        Assert.AreEqual(5, Scalar(session, "SELECT COUNT(*) FROM dbo.PipeTarget;"));

        Exec(session, "DROP TABLE dbo.PipeA; DROP TABLE dbo.PipeB; DROP TABLE dbo.PipeTarget;");
    }

    [TestMethod]
    public void Deploy_is_idempotent() {
        var session = NewSession();
        var dir = Path.Combine(Path.GetTempPath(), "clrdeploy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "01_proc.sql"), "CREATE PROCEDURE dbo.ClrDeployProc AS SELECT 1;");
            File.WriteAllText(Path.Combine(dir, "02_view.sql"), "CREATE VIEW dbo.ClrDeployView AS SELECT 1 AS X;");

            var first = session.Deploy("it", new DeployOptions { Path = dir });
            Assert.IsTrue(first.Success, "first deploy should succeed");
            Assert.AreEqual(2, first.Deployed);
            Assert.AreEqual(1, Scalar(session, "SELECT COUNT(*) FROM sys.objects WHERE name='ClrDeployProc';"));

            // Re-deploying the same folder must succeed (CREATE OR ALTER).
            var second = session.Deploy("it", new DeployOptions { Path = dir });
            Assert.IsTrue(second.Success, "re-deploy should be idempotent");
        } finally {
            Exec(session, "IF OBJECT_ID('dbo.ClrDeployView') IS NOT NULL DROP VIEW dbo.ClrDeployView;");
            Exec(session, "IF OBJECT_ID('dbo.ClrDeployProc') IS NOT NULL DROP PROCEDURE dbo.ClrDeployProc;");
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Sql_bulk_magic_copies_between_connections() {
        var session = NewSession();
        Exec(session, "IF OBJECT_ID('dbo.ClrMagicSrc') IS NOT NULL DROP TABLE dbo.ClrMagicSrc;");
        Exec(session, "IF OBJECT_ID('dbo.ClrMagicDst') IS NOT NULL DROP TABLE dbo.ClrMagicDst;");
        Exec(session, "CREATE TABLE dbo.ClrMagicSrc (Id INT, V NVARCHAR(20));");
        Exec(session, "CREATE TABLE dbo.ClrMagicDst (Id INT, V NVARCHAR(20));");
        Exec(session, "INSERT INTO dbo.ClrMagicSrc VALUES (1,'x'),(2,'y');");

        var display = session.ExecuteBulk(
            "#!sql-bulk --from it --from-table dbo.ClrMagicSrc --to it --table dbo.ClrMagicDst --no-progress");
        Assert.IsNotNull(display);
        Assert.AreEqual(2, Scalar(session, "SELECT COUNT(*) FROM dbo.ClrMagicDst;"));

        Exec(session, "DROP TABLE dbo.ClrMagicSrc; DROP TABLE dbo.ClrMagicDst;");
    }
}
