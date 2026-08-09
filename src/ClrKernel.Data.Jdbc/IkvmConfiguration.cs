using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClrKernel.Data.Jdbc;

// Ported from Integrator.Databases.Jdbc. Locates IKVM.Home in the NuGet cache so the
// embedded JVM can load. NOTE: the architecture folder defaults to "win-x64", so this
// is effectively Windows-only as written; adjust for other platforms if needed.
public static class IkvmConfiguration {
    public record IkvmPaths(
        Either<FileInfo> IkvmAssemblyBase,
        Either<DirectoryInfo> IkvmPackage,
        Either<DirectoryInfo> IkvmMsbuildPackage,
        Either<DirectoryInfo> IkvmHome);

    public record Either<T>(T Value, Exception Error);

    public static Either<X> ToEither<X>(Func<X> load) {
        try {
            return new Either<X>(Value: load(), Error: null);
        } catch (Exception e) {
            return new Either<X>(Value: default, Error: e);
        }
    }

    public static bool HasCustomIkvmHome => AppContext.GetData("IKVM.Home") is not null;
    public static bool HasIkvmPropertiesFile => File.Exists("ikvm.properties");
    public static string IkvmSearchAssemblyLocation => typeof(IKVM.Reflection.Assembly).Assembly.Location;
    private static readonly Lazy<Either<Assembly>> _ikvmAssembly = new(() => ToEither(() => typeof(IKVM.Runtime.JVM).Assembly));

    public static bool IkvmIsInitialized => _ikvmAssembly.Value.Value != null;
    public static Exception IkvmLoadError => _ikvmAssembly.Value.Error;

    public static void EnsureConfigured() {
        if (HasCustomIkvmHome || HasIkvmPropertiesFile) {
            return;
        }

        var paths = PathsFromNugetCache();
        var (path, error) = paths.IkvmHome;
        switch (path) {
            case FileSystemInfo i: AppContext.SetData("IKVM.Home", i.FullName); break;
            default: throw new Exception($"Unable to configure IKVM.Home: {error?.Message ?? "Invalid state."}\n{error?.StackTrace}");
        }

        if (IkvmLoadError is Exception e) {
            throw new Exception(
                $"IkvmConfiguration.EnsureConfigured: Unable to load IKVM assembly using nuget paths ({paths}):\n" +
                $"IKVM.Home: {AppContext.GetData("IKVM.Home")}\nError: {e.Message}\n    {e.StackTrace}");
        }
    }

    public static IkvmPaths PathsFromNugetCache() =>
        new IkvmPaths(
            IkvmAssemblyBase: ToEither(() => new FileInfo(IkvmSearchAssemblyLocation)),
            IkvmPackage: ToEither(() => FindIkvmPackage(IkvmSearchAssemblyLocation)),
            IkvmMsbuildPackage: ToEither(() => FindIkvmMsbuildPackage(IkvmSearchAssemblyLocation)),
            IkvmHome: ToEither(() => FindIkvmHome(IkvmSearchAssemblyLocation)));

    private static DirectoryInfo FindIkvmHome(string assemblyFile, string architectureName = "win-x64") {
        var ikvmMsbuildPackage = FindIkvmMsbuildPackage(assemblyFile);
        var jvmDlls = ikvmMsbuildPackage.GetFiles("jvm.dll", SearchOption.AllDirectories);
        var homeDirectories = (
            from jvmDll in jvmDlls
            from folder in ListAllParents(jvmDll)
            where folder.Name == architectureName
            orderby folder.FullName descending
            select folder).ToArray();
        var latestVersion = homeDirectories.FirstOrDefault()
            ?? throw new Exception($"Unable to find architecture folder '{architectureName}' for 'jvm.dll': {string.Join(", ", jvmDlls.Select(fi => fi.FullName))}");
        var versionFolder = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var exactVersion = homeDirectories.FirstOrDefault(folder => folder.FullName.Contains(versionFolder));
        return exactVersion ?? latestVersion;
    }

    private static DirectoryInfo FindIkvmMsbuildPackage(string assemblyFile) {
        var ikvmPackage = FindIkvmPackage(assemblyFile);
        var ikvmPackageVersion = ikvmPackage.Name;
        var nugetPackageRoot = ikvmPackage.Parent?.Parent
            ?? throw new Exception($"Unable to find nuget package root from '{ikvmPackage}'");
        var ikvmMsbuildRoot = nugetPackageRoot.EnumerateDirectories("ikvm.msbuild").FirstOrDefault()
            ?? throw new Exception($"Unable to find 'ikvm.msbuild' in '{nugetPackageRoot}'");
        return ikvmMsbuildRoot.EnumerateDirectories(ikvmPackageVersion).FirstOrDefault()
            ?? throw new Exception($"Unable to find 'ikvm.msbuild' version '{ikvmPackageVersion}' in '{ikvmMsbuildRoot}'");
    }

    private static DirectoryInfo FindIkvmPackage(string assemblyFile) =>
        (from folder in ListAllParents(new FileInfo(assemblyFile))
         where folder.Parent?.Name == "ikvm"
         select folder).FirstOrDefault()
        ?? throw new Exception($"FindIkvmPackage: unable to find package starting from '{assemblyFile}'");

    private static System.Collections.Generic.IEnumerable<DirectoryInfo> ListAllParents(FileInfo file) {
        var dir = file.Directory;
        while (dir is not null) {
            yield return dir;
            dir = dir.Parent;
        }
    }
}
