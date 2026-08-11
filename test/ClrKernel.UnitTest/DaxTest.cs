using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.AnalysisServices;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class DaxDirectiveTest {
    [TestMethod]
    public void ParseConnect_integrated_by_default() {
        var d = DaxDirectives.ParseConnect("#!dax-connect --name analytics --server DataWarehouseServer01.yourdomain.local --database AdventureWorksDW2025 --default");
        Assert.AreEqual("analytics", d.Name);
        Assert.IsTrue(d.IsDefault);
        Assert.AreEqual(SsasAuthMode.Integrated, d.Spec.Auth);
        Assert.AreEqual("DataWarehouseServer01.yourdomain.local", d.Spec.Server);
        Assert.AreEqual("AdventureWorksDW2025", d.Spec.Database);
    }

    [TestMethod]
    public void ParseConnect_fabric_builds_powerbi_endpoint() {
        var d = DaxDirectives.ParseConnect("#!dax-connect --name sales --fabric --workspace \"Analytics WS\" --model \"Sales Model\"");
        Assert.AreEqual(SsasAuthMode.AzureAd, d.Spec.Auth);
        Assert.AreEqual("powerbi://api.powerbi.com/v1.0/myorg/Analytics WS", d.Spec.Server);
        Assert.AreEqual("Sales Model", d.Spec.Database);
        Assert.IsNotNull(d.Spec.TokenProvider);
    }

    [TestMethod]
    public void ParseConnect_user_resolves_secret_from_env() {
        Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_DAX_TEST", "s3cret");
        try {
            var d = DaxDirectives.ParseConnect("#!dax-connect --name c --server s --database d --user svc --secret dax-test");
            Assert.AreEqual(SsasAuthMode.UserPassword, d.Spec.Auth);
            Assert.AreEqual("svc", d.Spec.User);
            Assert.AreEqual("s3cret", d.Spec.Password);
        } finally {
            Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_DAX_TEST", null);
        }
    }

    [TestMethod]
    public void ParseConnect_rejects_inline_password_and_requires_name() {
        Assert.ThrowsExactly<FormatException>(() =>
            DaxDirectives.ParseConnect("#!dax-connect --name c --server s --user u --password hunter2"));
        Assert.ThrowsExactly<FormatException>(() =>
            DaxDirectives.ParseConnect("#!dax-connect --server s"));
    }

    [TestMethod]
    public void ParseCell_reads_connection_comment_and_inline() {
        Assert.AreEqual("analytics", DaxDirectives.ParseCell("-- connections analytics\nEVALUATE 'Sales'").CubeName);
        Assert.AreEqual("wh", DaxDirectives.SelectorConnection("#!dax --connections wh"));
        Assert.IsNull(DaxDirectives.ParseCell("EVALUATE 'Sales'").CubeName);
    }
}

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

[TestClass]
public class DaxEngineRoutingTest {
    [TestMethod]
    public async Task Dax_connect_registers_a_named_cube() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var result = await engine.ExecuteAsync("#!dax-connect --name analytics --server ssas --database DW --default");
        var dd = result as DisplayData;
        Assert.IsNotNull(dd);
        StringAssert.Contains((string)dd.Data["text/plain"], "analytics");
        Assert.IsTrue(engine.Cubes.Cubes.TryGet("analytics", out _));
        Assert.AreEqual("analytics", engine.Cubes.Cubes.DefaultName);
    }

    [TestMethod]
    public async Task Dax_cell_without_a_cube_reports_guidance() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var threw = false;
        try {
            await engine.ExecuteAsync("#!dax\nEVALUATE 'Sales'");
        } catch (InvalidOperationException e) {
            threw = true;
            StringAssert.Contains(e.Message, "cube");
        }
        Assert.IsTrue(threw);
    }
}

[TestClass]
public class DaxCompletionTest {
    private static DaxCompletionContext Ctx() => new DaxCompletionContext {
        CubeNames = new[] { "analytics", "warehouse" },
    };

    private static System.Collections.Generic.List<string> Labels(string code) =>
        DaxLanguage.Complete(code, code.Length, Ctx()).Items.Select(i => i.Label).ToList();

    [TestMethod]
    public void Completes_magics_flags_and_cubes() {
        CollectionAssert.Contains(Labels("#!dax-"), "#!dax-connect");
        CollectionAssert.Contains(Labels("#!dax-connect --f"), "--fabric");
        CollectionAssert.Contains(Labels("#!dax --connections "), "analytics");
    }

    [TestMethod]
    public void Completes_directive_and_dax_functions() {
        CollectionAssert.Contains(Labels("-- connections "), "warehouse");
        CollectionAssert.Contains(Labels("CALC"), "CALCULATE");
        CollectionAssert.Contains(Labels("EVAL"), "EVALUATE");
    }
}

[TestClass]
public class DaxImportTest {
    [TestMethod]
    public void Markdown_dax_fence_and_connect_passthrough() {
        var query = NotebookImporter.ParseMarkdown("```dax\nEVALUATE 'Sales'\n```\n");
        Assert.AreEqual(1, query.Count);
        StringAssert.StartsWith(query[0], "#!dax\n");

        var connect = NotebookImporter.ParseMarkdown("```dax\n#!dax-connect --name a --server s\n```\n");
        StringAssert.StartsWith(connect[0], "#!dax-connect");
    }

    [TestMethod]
    public void Dib_dax_section_becomes_dax_block() {
        var blocks = NotebookImporter.ParseDib("#!csharp\nvar x = 1;\n#!dax\nEVALUATE 'Sales'\n");
        Assert.AreEqual(2, blocks.Count);
        StringAssert.StartsWith(blocks[1], "#!dax\n");
    }
}
