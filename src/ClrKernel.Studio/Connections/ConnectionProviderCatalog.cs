using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Database.Provider.Odbc;
using ClrKernel.Database.Provider.Oracle;
using ClrKernel.Database.Provider.Postgres;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// The connection types this server offers, asked of the kernel rather than listed
/// here.
/// <para>
/// The list used to be one entry long and written in the source, which made the
/// connection form a SQL Server form wearing a generic one's clothes. The kernel
/// already knows what it can connect to — every provider describes itself, and the
/// <c>#r</c>-loaded ones describe themselves the moment they are loaded — so this
/// asks it once and caches the answer, the way <see cref="KernelLanguages"/> does
/// for cell languages and for the same reason: the probe costs a short-lived
/// process and the answer does not change while the kernel binary does not.
/// </para>
/// <para>
/// Being offered is not the same as being queryable. A connection whose provider
/// lives only inside a kernel session can be saved and named by a notebook, which
/// is what makes the store worth having; the Connections area's own tree and query
/// editor open connections in *this* process and so reach only the providers it
/// references. The metadata route says <c>supported: false</c> for the rest rather
/// than pretending, and that is now a path somebody can actually reach.
/// </para>
/// </summary>
public sealed class ConnectionProviderCatalog {
    /// <summary>
    /// What is available before the kernel has been asked, and if it cannot be.
    /// <para>
    /// Every type this process can open itself, because it references those provider
    /// packages: the Connections area works whether or not a kernel is installed, and
    /// a server whose kernel is missing should still be able to save and query the
    /// connections it already has.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<ConnectionProviderDescriptor> _builtIn =
        new[] {
            SqlServerConnectionProvider.Descriptor,
            PostgresConnectionProvider.Descriptor,
            OracleConnectionProvider.Descriptor,
            OdbcConnectionProvider.Descriptor,
        };

    private readonly JobsOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task<IReadOnlyList<ConnectionProviderDescriptor>> _probe;
    private volatile IReadOnlyList<ConnectionProviderDescriptor> _known = _builtIn;

    public ConnectionProviderCatalog(JobsOptions options, ILogger<ConnectionProviderCatalog> logger) {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The last answer, without waiting for one.
    /// <para>
    /// For the callers that cannot await — reading a notebook for the connections it
    /// names while deciding whether it may be promoted. Before the first probe this
    /// is the built-in list, which is the conservative answer: fewer known providers
    /// means fewer connect directives classified, and an unclassified one is left
    /// alone rather than blocked.
    /// </para>
    /// </summary>
    public IReadOnlyList<ConnectionProviderDescriptor> Known => _known;

    /// <summary>Every provider the kernel declares, probed once per process.</summary>
    public Task<IReadOnlyList<ConnectionProviderDescriptor>> GetAsync(
        CancellationToken cancellationToken = default) {
        lock (_gate) {
            return _probe ??= ProbeAsync();
        }
    }

    /// <summary>Drops the cached answer — a kernel that has since been installed, or
    /// upgraded, supersedes it.</summary>
    public void Invalidate() {
        lock (_gate) {
            _probe = null;
        }
    }

    /// <summary>The descriptor for a <c>$type</c>, or null.</summary>
    public ConnectionProviderDescriptor Find(string type) =>
        _known.FirstOrDefault(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this process can itself open a connection of this type — which is what
    /// decides if it can be browsed and queried rather than only saved. Asked of the
    /// dialects rather than of a second list, so "we know its settings" and "we can
    /// open it" cannot drift apart.
    /// </summary>
    public static bool IsQueryable(string type) => ConnectionDialects.Supports(type);

    private async Task<IReadOnlyList<ConnectionProviderDescriptor>> ProbeAsync() {
        try {
            using var kernel = KernelProcess.Start(_options.ClrKernelPath, Environment.CurrentDirectory,
                line => _logger.LogDebug("kernel: {Line}", line));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await kernel.Client.InitializeAsync(timeout.Token).ConfigureAwait(false);
            // No language: the providers that belong to no cell language — Fabric,
            // Oracle, ODBC, JDBC are used from C# cells — are exactly the ones a
            // per-language question can never reach.
            var reply = await kernel.Client
                .DescribeConnectionsAsync(languageId: null, notebookUri: null, timeout.Token)
                .ConfigureAwait(false);
            await kernel.Client.ShutdownAsync().ConfigureAwait(false);

            var found = Merge(reply?.Providers);
            _known = found;
            _logger.LogInformation(
                "The kernel offers {Count} connection types: {Types}.",
                found.Count, string.Join(", ", found.Select(p => p.Type)));
            return found;
        } catch (Exception e) {
            // A kernel that cannot start, or one too old to answer, leaves the types
            // this process can open itself — which is the degraded mode, not a fault.
            _logger.LogWarning(
                "Could not read the kernel's connection types ({Error}); offering only the built-in ones.",
                e.Message);
            _known = _builtIn;
            return _builtIn;
        }
    }

    /// <summary>
    /// The kernel's answer, with the built-ins guaranteed present.
    /// <para>
    /// A kernel that answers without SQL Server — an old one, or one built without
    /// the provider — must not take away a type this server can open and may already
    /// have connections saved for. The kernel's own descriptor wins where both have
    /// one, because it is the one whose settings the notebook will be parsed against.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ConnectionProviderDescriptor> Merge(
        IReadOnlyList<ConnectionProviderDescriptor> fromKernel) {
        var found = (fromKernel ?? Array.Empty<ConnectionProviderDescriptor>())
            .Where(p => !string.IsNullOrWhiteSpace(p?.Type))
            .ToList();
        foreach (var builtIn in _builtIn) {
            if (!found.Any(p => string.Equals(p.Type, builtIn.Type, StringComparison.OrdinalIgnoreCase))) {
                found.Add(builtIn);
            }
        }
        return found
            .OrderBy(p => IsQueryable(p.Type) ? 0 : 1)
            .ThenBy(p => p.DisplayName ?? p.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
