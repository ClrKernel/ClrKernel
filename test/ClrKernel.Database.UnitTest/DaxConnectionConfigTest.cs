using System.IO;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Language.Dax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Cubes round-trip through the same <c>connections.json</c> the SQL connections use, told apart
/// by <c>"$type": "AnalysisServices"</c>. All offline — writing and reading a file touches no server.
/// </summary>
[TestClass]
public class DaxConnectionConfigTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-dax-cfg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Teardown() => Directory.Delete(_dir, recursive: true);

    private string File_ => Path.Combine(_dir, "connections.json");

    [TestMethod]
    public void A_fabric_cube_round_trips_and_keeps_its_token_provider() {
        var save = new SsasSession();
        save.Connect("#!dax-connect --name fcst --fabric --workspace DataWarehouse --model Forecast");
        save.SaveConnectionToConfig("fcst", File_);

        var json = System.IO.File.ReadAllText(File_);
        StringAssert.Contains(json, "\"AnalysisServices\"", "the $type must distinguish it from SqlServer nodes");
        StringAssert.Contains(json, "powerbi://api.powerbi.com/v1.0/myorg/DataWarehouse");

        var load = new SsasSession();
        CollectionAssert.AreEqual(new[] { "fcst" }, load.LoadFromConfig(_dir).ToArray());
        var spec = load.Cubes.Resolve("fcst");
        Assert.AreEqual(SsasAuthMode.AzureAd, spec.Auth);
        Assert.AreEqual("Forecast", spec.Database);
        // A delegate can't be serialised, so loading has to rebuild it. Without this the spec
        // attaches no token and fails with ADOMD's opaque "all authenticators" message.
        Assert.IsNotNull(spec.TokenProvider, "an Entra cube must come back with a token provider");
    }

    [TestMethod]
    public void A_windows_auth_fabric_cube_round_trips_without_gaining_a_token() {
        var save = new SsasSession();
        save.Connect("#!dax-connect --name w --fabric --workspace DataWarehouse --model Forecast --integrated");
        save.SaveConnectionToConfig("w", File_);

        var load = new SsasSession();
        load.LoadFromConfig(_dir);
        var spec = load.Cubes.Resolve("w");
        Assert.AreEqual(SsasAuthMode.Integrated, spec.Auth);
        Assert.IsNull(spec.TokenProvider);
        Assert.AreEqual("powerbi://api.powerbi.com/v1.0/myorg/DataWarehouse", spec.Server);
    }

    [TestMethod]
    public void An_azure_analysis_services_cube_round_trips_on_its_own_scope() {
        var save = new SsasSession();
        save.Connect("#!dax-connect --name aas --server \"asazure://westus.asazure.windows.net/srv\" --database M --azure-as");
        save.SaveConnectionToConfig("aas", File_);

        var load = new SsasSession();
        load.LoadFromConfig(_dir);
        var spec = load.Cubes.Resolve("aas");
        Assert.AreEqual(SsasAuthMode.AzureAd, spec.Auth);
        Assert.AreEqual("asazure://westus.asazure.windows.net/srv", spec.Server);
        // FromNode picks the scope from the endpoint, so an asazure:// cube must not come back
        // asking Entra for a Power BI token.
        Assert.IsNotNull(spec.TokenProvider);
    }

    [TestMethod]
    public void Every_cube_kind_the_button_can_create_is_saveable() {
        // The button offers Fabric, Azure AS and on-prem; all three must survive the round trip,
        // because the save prompt is offered for all three.
        var save = new SsasSession();
        save.Connect("#!dax-connect --name fab --fabric --workspace WS --model M");
        save.Connect("#!dax-connect --name aas --server \"asazure://r.asazure.windows.net/s\" --database M --azure-as");
        save.Connect("#!dax-connect --name onprem --server ssas01 --database M");
        foreach (var name in new[] { "fab", "aas", "onprem" }) {
            save.SaveConnectionToConfig(name, File_);
        }

        var loaded = new SsasSession().LoadFromConfig(_dir);
        CollectionAssert.AreEquivalent(new[] { "fab", "aas", "onprem" }, loaded.ToArray());
    }

    [TestMethod]
    public void A_password_is_written_as_a_reference_never_as_itself() {
        System.Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SSAS_APP", "hunter2");
        try {
            var save = new SsasSession();
            save.Connect("#!dax-connect --name u --server ssas01 --database M --user svc --secret ssas:app");
            save.SaveConnectionToConfig("u", File_);

            var json = System.IO.File.ReadAllText(File_);
            Assert.IsFalse(json.Contains("hunter2"), "the password must never reach the file");
            StringAssert.Contains(json, "ssas:app");

            var load = new SsasSession();
            load.LoadFromConfig(_dir);
            var spec = load.Cubes.Resolve("u");
            Assert.AreEqual("svc", spec.User);
            Assert.AreEqual("hunter2", spec.Password, "resolved from the store on load, as a directive would");
        } finally {
            System.Environment.SetEnvironmentVariable("CLRKERNEL_SECRET_SSAS_APP", null);
        }
    }

    [TestMethod]
    public void Sql_entries_in_the_same_file_are_skipped_not_misread() {
        var sql = new ClrKernel.Language.Sql.SqlSession();
        sql.Connect("#!sql-connect --name wh --server dw --database reports");
        sql.SaveConnectionToConfig("wh", File_);

        var dax = new SsasSession();
        dax.Connect("#!dax-connect --name fcst --fabric --workspace WS --model M");
        dax.SaveConnectionToConfig("fcst", File_);

        // One file, both kinds, each loader taking only its own.
        Assert.AreEqual(0, new SsasSession().LoadFromConfig(_dir).Count(n => n == "wh"));
        CollectionAssert.AreEqual(new[] { "fcst" }, new SsasSession().LoadFromConfig(_dir).ToArray());
        CollectionAssert.AreEqual(new[] { "wh" }, new ClrKernel.Language.Sql.SqlSession().LoadFromConfig(_dir).ToArray());
    }

    [TestMethod]
    public void The_catalog_now_advertises_config_support_to_the_host() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // The JSON-RPC host type-checks for this; it needed no change to start offering the
        // config methods for DAX.
        Assert.IsInstanceOfType(engine.Languages.ById("dax").Connections, typeof(IConfigBackedConnections));
    }
}
