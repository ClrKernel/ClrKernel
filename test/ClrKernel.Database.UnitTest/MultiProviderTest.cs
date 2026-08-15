using System;
using System.Collections.Generic;
using System.IO;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Provider-agnostic core (ClrKernel.Database.DataSource) exercised end-to-end over a real
/// ADO.NET provider that is NOT SQL Server — SQLite in-memory — so the shared query /
/// results / table / transaction path is validated without a server.
/// </summary>
[TestClass]
public class CoreDatabaseTest {
    private SqliteConnection _keepAlive;
    private DataSource _db;

    [TestInitialize]
    public void Setup() {
        // Unique shared in-memory DB per test; stays alive as long as one connection is open.
        var cs = $"Data Source=file:core-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        _db = new DataSource("sqlite", () => new SqliteConnection(cs));
        _db.Execute("create table Person (Id integer, Name text, Amount real)");
    }

    [TestCleanup]
    public void Teardown() => _keepAlive?.Dispose();

    public record Person(long Id, string Name, double Amount);

    [TestMethod]
    public void Insert_query_grid_rows_typed_scalar() {
        var inserted = _db.Table("Person").Insert(new[] {
            new { Id = 1, Name = "Ann", Amount = 10.5 },
            new { Id = 2, Name = "Ben", Amount = 20.0 },
        });
        Assert.AreEqual(2, inserted);

        var results = _db.Query("select * from Person order by Id").Results();
        Assert.IsInstanceOfType(results, typeof(IDisplayValue)); // renders as the grid
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("Ann", (string)results[0].Name);          // dynamic row access

        var people = _db.Query("select * from Person order by Id").Results<Person>();
        Assert.AreEqual(20.0, people[1].Amount);

        Assert.AreEqual(2, _db.Table("Person").Count());
        Assert.AreEqual(2L, _db.Scalar<long>("select count(*) from Person"));
    }

    [TestMethod]
    public void Insert_accepts_dictionary_rows() {
        var rows = new[] {
            new Dictionary<string, object> { ["Id"] = 5L, ["Name"] = "Cy", ["Amount"] = 1.0 },
        };
        Assert.AreEqual(1, _db.Table("Person").Insert(rows));
        Assert.AreEqual("Cy", _db.Scalar<string>("select Name from Person where Id = 5"));
    }

    [TestMethod]
    public void Parameters_bind_by_name() {
        _db.Table("Person").Insert(new[] { new { Id = 9, Name = "Zed", Amount = 3.0 } });
        var r = _db.Query("select Name from Person where Id = @id", new { id = 9 }).Results();
        Assert.AreEqual("Zed", (string)r[0].Name);
    }

    [TestMethod]
    public void Transaction_commits_and_rolls_back() {
        using (var tx = _db.Transaction()) {
            tx.Execute("insert into Person (Id, Name) values (1, 'a')");
            // no commit -> rollback on dispose
        }
        Assert.AreEqual(0L, _db.Scalar<long>("select count(*) from Person"));

        using (var tx = _db.Transaction()) {
            tx.Execute("insert into Person (Id, Name) values (2, 'b')");
            tx.Commit();
        }
        Assert.AreEqual(1L, _db.Scalar<long>("select count(*) from Person"));
    }
}

[TestClass]
public class ConnectionConfigTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Teardown() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string dir, string json) => File.WriteAllText(Path.Combine(dir, "connections.json"), json);

    [TestMethod]
    public void Loads_properties_and_resolves_secret_from_env() {
        Write(_dir, """
        {
          "erp": {
            "$type": "Oracle",
            "server": "orahost", "port": 1521, "serviceName": "ORCL",
            "userId": "scott",
            "password": { "secret": "oracle:erp" }
          }
        }
        """);
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ORACLE_ERP", "tiger");
        try {
            var config = ConnectionConfig.Load("erp", new SecretStore(), startDirectory: _dir).EnsureType("Oracle");
            Assert.AreEqual("orahost", config.Get("server"));
            Assert.AreEqual(1521, config.GetInt("port"));
            Assert.AreEqual("tiger", config.Get("password"));   // resolved from the env-var secret
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ORACLE_ERP", null);
        }
    }

    [TestMethod]
    public void Inherit_continues_search_up_the_tree() {
        var child = Path.Combine(_dir, "child");
        Directory.CreateDirectory(child);
        Write(_dir, """{ "db": { "$type": "Odbc", "connectionString": "DSN=parent" } }""");
        Write(child, """{ "db": "inherit" }""");
        var config = ConnectionConfig.Load("db", new SecretStore(), startDirectory: child);
        Assert.AreEqual("DSN=parent", config.Get("connectionString"));
    }

    [TestMethod]
    public void EnsureType_rejects_wrong_type() {
        Write(_dir, """{ "db": { "$type": "Odbc", "connectionString": "x" } }""");
        var config = ConnectionConfig.Load("db", new SecretStore(), startDirectory: _dir);
        Assert.ThrowsExactly<ConnectionConfigException>(() => config.EnsureType("Oracle"));
    }

    [TestMethod]
    public void Missing_connection_throws() {
        Write(_dir, """{ "other": { "$type": "Odbc" } }""");
        Assert.ThrowsExactly<ConnectionConfigException>(
            () => ConnectionConfig.Load("db", new SecretStore(), startDirectory: _dir));
    }
}

[TestClass]
public class ProviderFactoryTest {
    [TestMethod]
    public void Oracle_from_connection_string_builds_a_database() {
        var db = ClrKernel.Database.Provider.Oracle.Oracle.FromConnectionString("User Id=scott;Password=x;Data Source=orcl", "erp");
        Assert.AreEqual("erp", db.Name);
        Assert.IsInstanceOfType(db, typeof(DataSource));
    }

    [TestMethod]
    public void Oracle_connect_requires_a_secret_ref() {
        Assert.ThrowsExactly<ArgumentException>(
            () => ClrKernel.Database.Provider.Oracle.Oracle.Connect("h", 1521, "ORCL", "scott", secretRef: null));
    }

    [TestMethod]
    public void Odbc_from_connection_string_builds_a_database() {
        var db = ClrKernel.Database.Provider.Odbc.Odbc.FromConnectionString("Driver={x};Server=h;Database=d;", "warehouse");
        Assert.AreEqual("warehouse", db.Name);
    }

    [TestMethod]
    public void Odbc_from_dsn_applies_secret_password() {
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ODBC_PW", "s3cret");
        try {
            var db = ClrKernel.Database.Provider.Odbc.Odbc.FromDsn("MyDsn", "svc", "odbc:pw");
            Assert.AreEqual("MyDsn", db.Name);   // building doesn't connect; secret resolved into the string
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_ODBC_PW", null);
        }
    }
}
