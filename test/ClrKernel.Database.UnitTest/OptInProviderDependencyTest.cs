using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// An opt-in provider arrives by <c>#r "nuget:"</c> in a kernel that already carries some of
/// the provider's dependencies. The default load context keeps the kernel's copy, so a provider
/// compiled against a newer one cannot bind to it at all.
/// </summary>
[TestClass]
public class OptInProviderDependencyTest {
    /// <summary>
    /// ClrKernel carries System.Data.Odbc 8.0.1 (Microsoft.PowerShell.SDK → Microsoft.Windows
    /// .Compatibility). 0.14.0's ODBC provider was built against 9.0.0, and every connection it
    /// opened failed with <c>FileNotFoundException: System.Data.Odbc, Version=9.0.0.0</c>. The
    /// tests reach the provider by project reference, where the two copies unify, so nothing
    /// here could fail until this compared the two graphs.
    /// </summary>
    [TestMethod]
    public void The_odbc_provider_needs_no_newer_System_Data_Odbc_than_the_kernel_carries() {
        var wanted = PackageVersion("src/ClrKernel.Database.Provider.Odbc", "System.Data.Odbc");
        var carried = PackageVersion("src/ClrKernel", "System.Data.Odbc");
        Assert.IsNotNull(wanted, "the ODBC provider no longer depends on System.Data.Odbc; this test has nothing to guard");
        Assert.IsNotNull(carried, "the kernel no longer carries System.Data.Odbc; nothing can clash with the provider's");
        Assert.IsTrue(wanted <= carried,
            $"the ODBC provider references System.Data.Odbc {wanted} and the kernel loads {carried}: "
            + "#r \"nuget: ClrKernel.Database.Provider.Odbc\" would fail to load it in every notebook.");
    }

    private static Version PackageVersion(string project, string package) {
        var assets = Path.Combine(RepoRoot(), project, "obj", "project.assets.json");
        if (!File.Exists(assets)) {
            Assert.Inconclusive($"{assets} is missing; restore the solution first.");
        }
        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject()) {
            foreach (var library in target.Value.EnumerateObject()) {
                var slash = library.Name.IndexOf('/');
                if (slash > 0 && library.Name[..slash].Equals(package, StringComparison.OrdinalIgnoreCase)) {
                    return Version.Parse(library.Name[(slash + 1)..].Split('-')[0]);
                }
            }
        }
        return null;
    }

    private static string RepoRoot() {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "ClrKernel.slnx"))) {
                return dir.FullName;
            }
        }
        Assert.Inconclusive("not running from inside the repository");
        return null;
    }
}
