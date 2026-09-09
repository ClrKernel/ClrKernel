using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A <c>NuGet.Config</c> beside the notebook — or in any folder above it — is what
/// <c>#r "nuget:"</c> restores through. That is how a private feed is declared for
/// a repo rather than for a machine.
///
/// <para>
/// Offline: the feed is a folder of one <c>.nupkg</c>, packed from the
/// <c>PrivateGreeter</c> fixture, and the config lists only that folder. The source is
/// written as <c>%CLRKERNEL_TEST_FEED%</c> because that is the shape a repo's
/// credentials take — NuGet expands the variable, Studio sets
/// <c>CLRKERNEL_SECRET_*</c> per branch, and nothing secret is in the file.
/// </para>
/// </summary>
[TestClass]
public class NuGetConfigTest {
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "projects");

    /// <summary>A notebook folder with a config naming one folder feed, and the feed.</summary>
    private static string NotebookFolderWithFeed() {
        var root = Path.Combine(Path.GetTempPath(), "clrkernel-nugetconfig-test", Guid.NewGuid().ToString("N"));
        var feed = Path.Combine(root, "feed");
        var notebooks = Path.Combine(root, "notebooks", "reports");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(notebooks);

        var (code, output) = DotnetCli.Run(DotnetCli.Locate(), new[] {
            // Its own fixture, sharing neither assembly name nor namespace with
            // SimpleLib. Two suites in one process found both leaks: the runtime
            // unifies an assembly by simple name to the highest version loaded, and
            // the language service sees every loaded assembly — so this package as a
            // renamed SimpleLib broke ProjectReferenceTest twice over.
            "pack", Path.Combine(_fixtures, "PrivateGreeter", "PrivateGreeter.csproj"),
            "-p:PackageId=Private.Greeter", "-p:Version=1.2.3", "-o", feed,
            "--nologo", "-v:q", "-nodeReuse:false",
        }, null, TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, code, output);

        // Above the notebook's own folder, not in it: the nearest config wins and
        // the search walks up, the same as `dotnet restore` in a repo.
        Environment.SetEnvironmentVariable("CLRKERNEL_TEST_FEED", feed);
        File.WriteAllText(Path.Combine(root, "notebooks", "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="%CLRKERNEL_TEST_FEED%" />
              </packageSources>
            </configuration>
            """);
        return notebooks;
    }

    [TestMethod]
    public async Task A_NuGet_Config_above_the_notebook_is_where_packages_come_from() {
        InteractiveScriptEngine.RefsFilePath = null;
        // A private packages cache for this test. NuGet resolves from the global
        // cache before any source, so once the package has been restored once on a
        // machine, "not found without the config" could never be observed again —
        // and the control below is the half that proves the config is what did it.
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var cache = Path.Combine(Path.GetTempPath(), "clrkernel-nugetconfig-test", "cache-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", cache);
        try {
            var notebooks = NotebookFolderWithFeed();

            // Control first, while the cache is empty: the feed folder exists and
            // holds the package, but nothing names it as a source.
            var configPath = Path.Combine(Path.GetDirectoryName(notebooks), "NuGet.Config");
            var config = File.ReadAllText(configPath);
            File.Delete(configPath);
            var blind = new InteractiveScriptEngine(notebooks, NullLogger.Instance);
            await Assert.ThrowsExactlyAsync<Exception>(
                () => blind.ExecuteAsync("#r \"nuget: Private.Greeter, 1.2.3\""),
                "resolved with no NuGet.Config in reach — the feed came from somewhere else");

            File.WriteAllText(configPath, config);
            var engine = new InteractiveScriptEngine(notebooks, NullLogger.Instance);
            await engine.ExecuteAsync("#r \"nuget: Private.Greeter, 1.2.3\"");
            var result = (DisplayData)await engine.ExecuteAsync("PrivateGreeter.Hello.Say(\"feed\")");
            Assert.AreEqual("Hello, feed!", result.Data["text/plain"]?.ToString());
        } finally {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }
}
