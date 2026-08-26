using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClrKernel.Core.Secrets;

namespace ClrKernel.Database;

/// <summary>
/// Opens a named <c>connections.json</c> entry as a <see cref="DataSource"/>,
/// whatever provider backs it.
/// <para>
/// By convention rather than by registration: a provider package
/// <c>ClrKernel.Database.Provider.X</c> exposes a static
/// <c>X.FromConfig(string name, SecretStore secrets)</c>, and that is what gets
/// called for a node whose <c>$type</c> is <c>X</c>. The alternative — a registry
/// each provider writes itself into — needs the provider's code to have run
/// before anyone asks, and these packages are loaded by <c>#r</c> partway through
/// a session precisely so their drivers are not pulled in unless used. Reflection
/// asks at the moment of the question instead, which is the only moment the
/// answer is known.
/// </para>
/// <para>
/// The cost is that a provider which does not follow the convention is invisible
/// here. That is what <see cref="CanOpen"/> is for: callers ask first and say
/// something useful, rather than letting a MissingMethodException surface as
/// "connection not found".
/// </para>
/// </summary>
public static class DataSourceCatalog {
    /// <summary>Whether a <c>$type</c> can be opened right now — which for an
    /// opt-in provider means "has this session <c>#r</c>'d the package yet".</summary>
    public static bool CanOpen(string type) => Opener(type) != null;

    /// <summary>
    /// Opens the named connection through the provider its <c>$type</c> names.
    /// Throws with the <c>#r</c> line to add when the package is not loaded.
    /// </summary>
    public static DataSource Open(string type, string name, SecretStore secrets = null) {
        if (string.IsNullOrWhiteSpace(type)) {
            throw new ConnectionConfigException(
                $"Connection '{name}' has no \"$type\", so nothing knows how to open it.");
        }
        var opener = Opener(type)
            ?? throw new ConnectionConfigException(
                $"Connection '{name}' is a {type} connection, and this session cannot open one. " +
                $"Load the provider first:  #r \"nuget: {PackageFor(type)}\"");
        try {
            return (DataSource)opener.Invoke(null, new object[] { name, secrets });
        } catch (TargetInvocationException e) when (e.InnerException != null) {
            // The provider's own message — "no such node", "server is required" —
            // is the one worth showing. Rethrow it with its stack rather than
            // wrapping it in a reflection failure nobody can act on.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(e.InnerException).Throw();
            throw; // unreachable; the compiler cannot see that
        }
    }

    /// <summary>The package a <c>$type</c> lives in, for the message that asks for it.</summary>
    public static string PackageFor(string type) => "ClrKernel.Database.Provider." + type;

    /// <summary>Every <c>$type</c> this session could open right now.</summary>
    public static IReadOnlyList<string> Available() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => n != null && n.StartsWith(_packagePrefix, StringComparison.Ordinal))
            .Select(n => n.Substring(_packagePrefix.Length))
            .Where(t => Opener(t) != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private const string _packagePrefix = "ClrKernel.Database.Provider.";

    private static readonly Dictionary<string, MethodInfo> _openers =
        new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);

    private static MethodInfo Opener(string type) {
        if (string.IsNullOrWhiteSpace(type)) {
            return null;
        }
        lock (_openers) {
            // Cached on the way in, but a miss is never cached: the package may be
            // #r'd after the first cell asked for it, and remembering "no" would
            // make the answer depend on the order cells were run in.
            if (_openers.TryGetValue(type, out var cached)) {
                return cached;
            }
            var found = Find(type);
            if (found != null) {
                _openers[type] = found;
            }
            return found;
        }
    }

    private static MethodInfo Find(string type) {
        var assemblyName = PackageFor(type);
        var entryPoint = assemblyName + "." + type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var method = assembly.GetType(entryPoint, throwOnError: false, ignoreCase: true)
                ?.GetMethod(
                    "FromConfig", BindingFlags.Public | BindingFlags.Static,
                    binder: null, types: new[] { typeof(string), typeof(SecretStore) }, modifiers: null);
            if (method != null && typeof(DataSource).IsAssignableFrom(method.ReturnType)) {
                return method;
            }
        }
        return null;
    }
}
