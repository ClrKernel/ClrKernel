using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// The cell languages the kernel can run, for code paths that need them before
/// any notebook session exists — parsing a notebook into cells, and filling the
/// editor's language picker.
/// <para>
/// Answering costs one short-lived <c>clrkernel serve</c>, so it happens once per
/// process, lazily (never at startup — that would slow <c>serve</c> for everyone,
/// including installs that never open the editor). A kernel that cannot start
/// yields an empty list, which parses notebooks as C#-only: the same degraded
/// mode a pre-0.10 kernel produces, rather than a failure.
/// </para>
/// </summary>
public sealed class KernelLanguages {
    private readonly JobsOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task<IReadOnlyList<LanguageDescriptor>> _probe;

    public KernelLanguages(JobsOptions options, ILogger logger) {
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyList<LanguageDescriptor>> GetAsync(CancellationToken cancellationToken = default) {
        lock (_gate) {
            return _probe ??= ProbeAsync();
        }
    }

    /// <summary>Drops the cached answer — a live session's languagesChanged, or a
    /// kernel that has since been installed, supersedes it.</summary>
    public void Invalidate() {
        lock (_gate) {
            _probe = null;
        }
    }

    /// <summary>Seeds the cache from a session that has already initialized, so the
    /// editor never pays for a second probe.</summary>
    public void Seed(IReadOnlyList<LanguageDescriptor> languages) {
        if (languages is { Count: > 0 }) {
            lock (_gate) {
                _probe = Task.FromResult(languages);
            }
        }
    }

    private async Task<IReadOnlyList<LanguageDescriptor>> ProbeAsync() {
        try {
            using var kernel = KernelProcess.Start(_options.ClrKernelPath, Environment.CurrentDirectory,
                line => _logger.LogDebug("kernel: {Line}", line));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reply = await kernel.Client.InitializeAsync(timeout.Token).ConfigureAwait(false);
            await kernel.Client.ShutdownAsync().ConfigureAwait(false);
            return reply.Languages ?? Array.Empty<LanguageDescriptor>();
        } catch (Exception e) {
            // No kernel, or one too old to answer: notebooks parse as C#-only.
            _logger.LogWarning("Could not read the kernel's languages ({Error}); notebooks will parse as C# only.", e.Message);
            return Array.Empty<LanguageDescriptor>();
        }
    }
}
