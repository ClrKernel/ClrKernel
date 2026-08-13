using System;
using System.IO;
using ClrKernel.Database;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// <c>connections.local.json</c> is a personal, typically git-ignored overlay next to
/// the shared <c>connections.json</c>: its entries override same-named shared ones and
/// may add new ones, so real dev servers never have to be committed.
/// </summary>
[TestClass]
public class ConnectionConfigLocalOverlayTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "ck-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteBase(string json) => File.WriteAllText(Path.Combine(_dir, "connections.json"), json);
    private void WriteLocal(string json) => File.WriteAllText(Path.Combine(_dir, "connections.local.json"), json);

    [TestMethod]
    public void FindFiles_returns_base_then_local() {
        WriteBase("{}");
        WriteLocal("{}");
        var files = ConnectionConfig.FindFiles(_dir);
        Assert.AreEqual(2, files.Count);
        StringAssert.EndsWith(files[0], "connections.json");
        StringAssert.EndsWith(files[1], "connections.local.json");
    }

    [TestMethod]
    public void A_local_entry_overrides_the_shared_one_and_adds_new_names() {
        WriteBase("""
            {
              "advdw": { "$type": "SqlServer", "server": "shared-host", "database": "DW", "auth": "integrated" },
              "other": { "$type": "SqlServer", "server": "other-host", "database": "O", "auth": "integrated" }
            }
            """);
        WriteLocal("""
            {
              "advdw": { "$type": "SqlServer", "server": "192.168.0.9", "database": "DW", "auth": "sql", "user": "sa" },
              "devonly": { "$type": "SqlServer", "server": "localhost", "database": "Dev", "auth": "integrated" }
            }
            """);

        var session = new SqlSession();
        var loaded = session.LoadFromConfig(_dir);

        CollectionAssert.AreEquivalent(new[] { "advdw", "other", "devonly" }, (System.Collections.ICollection)loaded);
        Assert.IsTrue(session.Connections.TryGet("advdw", out var advdw));
        Assert.AreEqual("192.168.0.9", advdw.Server, "the local overlay must win");
        Assert.IsTrue(session.Connections.TryGet("other", out var other));
        Assert.AreEqual("other-host", other.Server, "shared entries without an override survive");
    }

    [TestMethod]
    public void A_local_file_alone_is_enough() {
        WriteLocal("""
            { "devonly": { "$type": "SqlServer", "server": "localhost", "database": "Dev", "auth": "integrated" } }
            """);
        var session = new SqlSession();
        CollectionAssert.AreEqual(new[] { "devonly" }, (System.Collections.ICollection)session.LoadFromConfig(_dir));
    }

    [TestMethod]
    public void The_nearest_directory_wins_over_a_parent() {
        WriteBase("""
            { "near": { "$type": "SqlServer", "server": "near-host", "database": "N", "auth": "integrated" } }
            """);
        var child = Path.Combine(_dir, "notebooks");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "connections.local.json"), """
            { "childonly": { "$type": "SqlServer", "server": "child-host", "database": "C", "auth": "integrated" } }
            """);

        // The child directory has a candidate, so the walk stops there.
        var session = new SqlSession();
        CollectionAssert.AreEqual(new[] { "childonly" }, (System.Collections.ICollection)session.LoadFromConfig(child));
    }
}
