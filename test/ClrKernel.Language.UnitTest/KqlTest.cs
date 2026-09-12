using System;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.Kusto;
using ClrKernel.Language.Kql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The parts of KQL cells that need no cluster: the connect directive, the per-cell
/// connection choice, the registry. A query against a live cluster is
/// <see cref="KqlLiveTest"/>.
/// </summary>
[TestClass]
public class KqlTest {
    [TestMethod]
    public void Connect_parses_entra_by_default_and_a_service_principal_when_asked() {
        var entra = KqlDirectives.ParseConnect("#!kql-connect --name help --cluster https://help.kusto.windows.net --database Samples --default");
        Assert.AreEqual("help", entra.Name);
        Assert.AreEqual("https://help.kusto.windows.net", entra.Spec.Cluster);
        Assert.AreEqual("Samples", entra.Spec.Database);
        Assert.AreEqual(KustoAuthMode.Entra, entra.Spec.Auth);
        Assert.IsTrue(entra.IsDefault);

        var interactive = KqlDirectives.ParseConnect("#!kql-connect -n h -s https://x.kusto.windows.net -d db --auth interactive");
        Assert.AreEqual(KustoAuthMode.Interactive, interactive.Spec.Auth);

        var sp = KqlDirectives.ParseConnect(
            "#!kql-connect --name svc --cluster https://x.kusto.windows.net --database db --tenant t --client-id c --secret KUSTO_SECRET");
        Assert.AreEqual(KustoAuthMode.ClientSecret, sp.Spec.Auth);
        Assert.AreEqual("KUSTO_SECRET", sp.Spec.SecretRef, "the reference is kept; the secret is not in the spec until resolved");

        Assert.ThrowsExactly<FormatException>(() => KqlDirectives.ParseConnect(
            "#!kql-connect --name svc --cluster https://x --database db --auth clientsecret"),
            "a service principal without its three parts is refused");
    }

    [TestMethod]
    public void A_secret_on_the_line_is_refused() {
        var e = Assert.ThrowsExactly<FormatException>(() => KqlDirectives.ParseConnect(
            "#!kql-connect --name svc --cluster https://x --database db --tenant t --client-id c --client-secret hunter2"));
        StringAssert.Contains(e.Message, "--secret");
    }

    [TestMethod]
    public void A_cell_names_its_connection_inline_or_in_a_leading_comment() {
        Assert.AreEqual("prod", KqlDirectives.ParseCell("#!kql --connections prod\nT | take 1").ConnectionName);
        Assert.AreEqual("prod", KqlDirectives.ParseCell("// connections prod\nT | take 1").ConnectionName);
        Assert.AreEqual("prod", KqlDirectives.ParseCell("// connection: prod\nT | take 1").ConnectionName);
        Assert.IsNull(KqlDirectives.ParseCell("T | take 1\n// connections late").ConnectionName, "only leading lines count");
    }

    [TestMethod]
    public void The_session_keeps_named_databases_and_a_default() {
        using var session = new KqlSession();
        Assert.AreEqual("a", session.Connect("#!kql-connect --name a --cluster https://a.kusto.windows.net --database d"));
        session.Connect("#!kql-connect --name b --cluster https://b.kusto.windows.net --database d --default");
        Assert.AreEqual("b", session.DefaultName);
        Assert.AreEqual("https://b.kusto.windows.net", session.Resolve(null).Cluster);
        Assert.AreEqual("https://a.kusto.windows.net", session.Resolve("a").Cluster);
        var missing = Assert.ThrowsExactly<InvalidOperationException>(() => session.Resolve("nope"));
        StringAssert.Contains(missing.Message, "Known: a, b");
        Assert.IsTrue(session.Remove("b"));
        Assert.AreEqual("a", session.DefaultName, "the default moves when its connection goes");
    }

    [TestMethod]
    public void The_language_describes_itself_for_the_editors() {
        ICellLanguage language = new KqlCellLanguage();
        CollectionAssert.AreEqual(new[] { "#!kql", "#!kql-connect" }, language.Selectors.ToList());
        Assert.AreEqual("#!kql", language.DefaultSelector);
        Assert.IsNotNull(language.Connections);
        Assert.AreEqual("Kusto", language.SupportedProviders.Single());
        Assert.AreEqual("Kusto", KustoConnectionProvider.Descriptor.Type);
        Assert.AreEqual("#!kql-connect", KustoConnectionProvider.Descriptor.ConnectSelector);
    }
}

/// <summary>
/// One query against a real cluster. Set <c>CLRKERNEL_TEST_KUSTO</c> to a cluster
/// URL — <c>https://help.kusto.windows.net</c> works for any Entra account — and
/// <c>CLRKERNEL_TEST_KUSTO_DB</c> (default <c>Samples</c>); the default Entra
/// chain signs in.
/// </summary>
[TestClass]
public class KqlLiveTest {
    [TestMethod]
    public void A_query_returns_the_primary_result_as_a_table() {
        var cluster = Environment.GetEnvironmentVariable("CLRKERNEL_TEST_KUSTO");
        if (string.IsNullOrWhiteSpace(cluster)) {
            Assert.Inconclusive("CLRKERNEL_TEST_KUSTO is not set.");
        }
        var database = Environment.GetEnvironmentVariable("CLRKERNEL_TEST_KUSTO_DB") ?? "Samples";
        using var session = new KqlSession();
        session.Connect($"#!kql-connect --name live --cluster {cluster} --database {database}");
        var table = session.Execute("print x = 1, y = \"two\"");
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("two", table.Rows[0]["y"]);
    }
}
