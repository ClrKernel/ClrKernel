using System;
using System.Linq;
using ClrKernel.Database.Provider.AnalysisServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class DaxRegistryTest {
    [TestMethod]
    public void Resolve_default_named_and_error() {
        var reg = new SsasConnectionRegistry();
        reg.Register("a", new SsasConnectionSpec { Server = "sa", Database = "da" }, asDefault: true);
        reg.Register("b", new SsasConnectionSpec { Server = "sb", Database = "db" });
        Assert.AreEqual("a", reg.Resolve(null).Server == "sa" ? "a" : "?");
        Assert.AreEqual("sb", reg.Resolve("b").Server);
        Assert.ThrowsExactly<InvalidOperationException>(() => reg.Resolve("ghost"));
    }

    [TestMethod]
    public void All_lists_entries_and_remove_updates_default() {
        var reg = new SsasConnectionRegistry();
        reg.Register("a", new SsasConnectionSpec { Server = "sa" }, asDefault: true);
        reg.Register("b", new SsasConnectionSpec { Server = "sb" });
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, reg.All.Select(e => e.Name).ToArray());
        Assert.IsTrue(reg.Remove("a"));
        Assert.AreEqual("b", reg.DefaultName, "removing the default promotes another cube");
    }
}
