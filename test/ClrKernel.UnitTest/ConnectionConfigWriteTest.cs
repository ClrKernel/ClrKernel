using System;
using System.IO;
using System.Linq;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlBulkCreateDirectiveTest {
    [TestMethod]
    public void Create_flag_sets_CreateIfMissing() {
        var d = SqlEtlDirectives.ParseBulk(
            "#!sql-bulk --from src --to dst --query \"select 1\" --table stg.X --create");
        Assert.IsTrue(d.Options.CreateIfMissing);
    }

    [TestMethod]
    public void Create_is_off_by_default() {
        var d = SqlEtlDirectives.ParseBulk("#!sql-bulk --from src --query \"select 1\" --table stg.X");
        Assert.IsFalse(d.Options.CreateIfMissing);
    }

    [TestMethod]
    public void Create_if_missing_alias_works() {
        var d = SqlEtlDirectives.ParseBulk("#!sql-bulk --from src --from-table dbo.X --table stg.X --create-if-missing");
        Assert.IsTrue(d.Options.CreateIfMissing);
    }
}

[TestClass]
public class ConnectionConfigWriteTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "ck-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void Upsert_creates_file_with_type_and_secret_ref() {
        var path = Path.Combine(_dir, "connections.json");
        ConnectionConfig.Upsert(path, "warehouse", "SqlServer", new[] {
            ConfigProperty.Plain("server", "dw.db.local"),
            ConfigProperty.Plain("database", "DW"),
            ConfigProperty.Plain("auth", "sql"),
            ConfigProperty.Secret("password", "sql:warehouse"),
        });

        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "\"$type\": \"SqlServer\"");
        StringAssert.Contains(text, "\"secret\": \"sql:warehouse\"");
        // The literal password must never be written.
        Assert.IsFalse(text.Contains("\"password\": \"") && !text.Contains("secret"),
            "password should only appear as a secret reference");

        var nodes = ConnectionConfig.LoadAllRaw(path);
        var node = nodes.Single();
        Assert.AreEqual("warehouse", node.Name);
        Assert.IsTrue(node.IsType("SqlServer"));
        Assert.AreEqual("dw.db.local", node.Get("server"));
        Assert.AreEqual("sql:warehouse", node.SecretRef("password"));
        Assert.IsNull(node.Get("password"), "secret props are not exposed as plain values");
    }

    [TestMethod]
    public void Upsert_preserves_other_entries_and_replaces_same_name() {
        var path = Path.Combine(_dir, "connections.json");
        ConnectionConfig.Upsert(path, "keep", "Oracle", new[] { ConfigProperty.Plain("server", "orahost") });
        ConnectionConfig.Upsert(path, "dw", "SqlServer", new[] { ConfigProperty.Plain("server", "old") });
        ConnectionConfig.Upsert(path, "dw", "SqlServer", new[] { ConfigProperty.Plain("server", "new") });

        var names = ConnectionConfig.ListNames(path);
        CollectionAssert.AreEquivalent(new[] { "keep", "dw" }, names.ToArray());

        var dw = ConnectionConfig.LoadAllRaw(path).Single(n => n.Name == "dw");
        Assert.AreEqual("new", dw.Get("server"));
        var keep = ConnectionConfig.LoadAllRaw(path).Single(n => n.Name == "keep");
        Assert.IsTrue(keep.IsType("Oracle"));
    }

    [TestMethod]
    public void FindFile_walks_up_the_tree() {
        var sub = Path.Combine(_dir, "a", "b", "c");
        Directory.CreateDirectory(sub);
        var path = Path.Combine(_dir, "connections.json");
        ConnectionConfig.Upsert(path, "dw", "SqlServer", new[] { ConfigProperty.Plain("server", "x") });

        var found = ConnectionConfig.FindFile(sub);
        Assert.AreEqual(Path.GetFullPath(path), Path.GetFullPath(found));
    }

    [TestMethod]
    public void FindFile_returns_null_when_absent() {
        Assert.IsNull(ConnectionConfig.FindFile(_dir));
    }
}

[TestClass]
public class SqlConnectionConfigMappingTest {
    [TestMethod]
    public void Spec_round_trips_through_config_node() {
        var spec = new SqlConnectionSpec {
            Name = "sales",
            Server = "sql01",
            Database = "AppDb",
            Auth = SqlAuthMode.SqlPassword,
            User = "reader",
            TrustServerCertificate = true,
        };

        var props = SqlConnectionConfig.ToProperties(spec);
        // Round-trip through a real file via the raw reader.
        var dir = Path.Combine(Path.GetTempPath(), "ck-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "connections.json");
        try {
            ConnectionConfig.Upsert(path, spec.Name, SqlConnectionConfig.TypeName, props);
            var node = ConnectionConfig.LoadAllRaw(path).Single();
            var back = SqlConnectionConfig.FromNode(node);

            Assert.AreEqual("sales", back.Name);
            Assert.AreEqual("sql01", back.Server);
            Assert.AreEqual("AppDb", back.Database);
            Assert.AreEqual(SqlAuthMode.SqlPassword, back.Auth);
            Assert.AreEqual("reader", back.User);
            Assert.IsTrue(back.TrustServerCertificate);
            // The password reference is kept, not resolved.
            Assert.AreEqual("sql:sales", back.EffectiveSecretRef);
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Integrated_auth_writes_no_password() {
        var spec = new SqlConnectionSpec { Name = "dw", Server = "s", Auth = SqlAuthMode.Integrated };
        var props = SqlConnectionConfig.ToProperties(spec);
        Assert.IsFalse(props.Any(p => p.IsSecret), "integrated auth has no password secret");
        Assert.AreEqual("integrated", props.Single(p => p.Key == "auth").Value);
    }
}

[TestClass]
public class SqlSessionConfigTest {
    [TestMethod]
    public void Save_then_load_makes_connection_available_in_a_new_session() {
        var dir = Path.Combine(Path.GetTempPath(), "ck-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "connections.json");
        try {
            // Session 1: register a connection and save it.
            var s1 = new SqlSession();
            s1.Connect("#!sql-connect --name analytics --server sql-wh --database reports --auth integrated");
            var written = s1.SaveConnectionToConfig("analytics", path);
            Assert.AreEqual(path, written);

            // Session 2: fresh registry, auto-load from the config directory.
            var s2 = new SqlSession();
            Assert.IsTrue(s2.Connections.IsEmpty);
            var loaded = s2.LoadFromConfig(dir);
            CollectionAssert.Contains(loaded.ToArray(), "analytics");
            Assert.IsTrue(s2.Connections.TryGet("analytics", out var spec));
            Assert.AreEqual("sql-wh", spec.Server);
            Assert.AreEqual(SqlAuthMode.Integrated, spec.Auth);
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void FindConfigFile_reports_presence() {
        var dir = Path.Combine(Path.GetTempPath(), "ck-find-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var session = new SqlSession();
            Assert.IsNull(session.FindConfigFile(dir));
            session.Connect("#!sql-connect --name dw --server s --auth integrated");
            session.SaveConnectionToConfig("dw", Path.Combine(dir, "connections.json"));
            Assert.IsNotNull(session.FindConfigFile(dir));
            CollectionAssert.Contains(session.ConfigConnectionNames(session.FindConfigFile(dir)).ToArray(), "dw");
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
