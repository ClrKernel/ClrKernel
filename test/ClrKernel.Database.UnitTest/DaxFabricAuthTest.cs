using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Language.Dax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A Fabric / Power BI XMLA endpoint accepts more than one kind of credential, and which one works
/// depends on the tenant. On an Entra-joined Windows machine <c>Integrated Security=SSPI</c> is
/// often the only thing that succeeds — Windows negotiates the identity itself, so conditional
/// access is satisfied and no app registration is involved. A token fetched in-process comes from a
/// generic developer application the tenant may refuse, whatever its audience.
/// <para>
/// So <c>--fabric</c> must reach both, and the endpoint URL must be identical either way: the only
/// difference is the credential.
/// </para>
/// </summary>
[TestClass]
public class DaxFabricAuthTest {
    private const string _endpoint = "powerbi://api.powerbi.com/v1.0/myorg/DataWarehouse";

    private static SsasConnectionSpec Spec(string directive) => DaxDirectives.ParseConnect(directive).Spec;

    [TestMethod]
    public void Fabric_defaults_to_an_Entra_token() {
        var spec = Spec("#!dax-connect --name f --fabric --workspace DataWarehouse --model Forecast");
        Assert.AreEqual(_endpoint, spec.Server);
        Assert.AreEqual("Forecast", spec.Database);
        Assert.AreEqual(SsasAuthMode.AzureAd, spec.Auth);
        Assert.IsNotNull(spec.TokenProvider);
    }

    [TestMethod]
    public void Fabric_with_integrated_uses_the_windows_identity_and_the_same_endpoint() {
        var spec = Spec("#!dax-connect --name f --fabric --workspace DataWarehouse --model Forecast --integrated");
        Assert.AreEqual(_endpoint, spec.Server, "the endpoint must not change with the credential");
        Assert.AreEqual("Forecast", spec.Database);
        Assert.AreEqual(SsasAuthMode.Integrated, spec.Auth);
        Assert.IsNull(spec.TokenProvider, "nothing should fetch a token on this path");
    }

    [TestMethod]
    public void A_powerbi_url_given_as_a_server_still_works_and_is_not_refused() {
        // Equivalent to the working --connection-string form, and the shape the cube button's
        // on-prem prompt produces. A guard that rejected this was wrong and has been removed.
        var spec = Spec($"#!dax-connect --name p --server \"{_endpoint}\" --database Forecast");
        Assert.AreEqual(SsasAuthMode.Integrated, spec.Auth);
        Assert.AreEqual(_endpoint, spec.Server);
    }

    [TestMethod]
    public void Azure_analysis_services_can_also_take_the_windows_identity() {
        const string server = "asazure://westus.asazure.windows.net/mysrv";
        var entra = Spec($"#!dax-connect --name a --server \"{server}\" --database M --azure-as");
        Assert.AreEqual(SsasAuthMode.AzureAd, entra.Auth);

        var windows = Spec($"#!dax-connect --name a --server \"{server}\" --database M --azure-as --integrated");
        Assert.AreEqual(SsasAuthMode.Integrated, windows.Auth);
        Assert.AreEqual(server, windows.Server);
    }

    [TestMethod]
    public void The_raw_connection_string_escape_hatch_is_untouched() {
        var spec = Spec("#!dax-connect --name fcst --connection-string " +
                        "\"Data Source=powerbi://api.powerbi.com/v1.0/myorg/DataWarehouse;Catalog=Forecast;Integrated Security=SSPI\"");
        Assert.AreEqual(SsasAuthMode.ConnectionString, spec.Auth);
        StringAssert.Contains(spec.BuildAdomdConnectionString(), "Integrated Security=SSPI");
    }
}
