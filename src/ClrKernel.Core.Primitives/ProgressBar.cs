using System.Diagnostics;

namespace ClrKernel.Core.Primitives;
/// <summary>
/// A live, updatable progress bar. Create one, call <see cref="Report"/> as work
/// advances, and <see cref="Done"/> when finished. Each update publishes the
/// <see cref="DisplayProgress"/> concept through a <see cref="DisplayCell"/>, so the
/// bar itself is drawn by the registered formatters (ClrKernel.Formatting.Html) and
/// updates keep flowing to the originating cell even from background callbacks.
/// <para>
/// Available to C# cells directly (e.g. a long loop) and used by SQL bulk copy.
/// </para>
/// </summary>
public sealed class ProgressBar {
    private readonly DisplayCell _cell;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private string _label;
    private long _total;

    /// <summary>Creates and shows a progress bar. A non-positive total renders an
    /// indeterminate "N processed" counter instead of a percentage.</summary>
    public ProgressBar(string label, long total = 0) {
        _label = label ?? string.Empty;
        _total = total;
        _cell = new DisplayProgress(_label, null, 0, _total).Display();
    }

    /// <summary>Updates the known total (e.g. once a row count is known).</summary>
    public void SetTotal(long total) {
        _total = total;
    }

    /// <summary>Reports current progress and re-renders the bar.</summary>
    public void Report(long current, string label = null) {
        if (label != null) {
            _label = label;
        }
        _cell.Update(new DisplayProgress(_label, null, current, _total));
    }

    /// <summary>Marks the bar complete (100%) with an optional final message.</summary>
    public void Done(long? finalCount = null, string message = null) {
        _stopwatch.Stop();
        var count = finalCount ?? _total;
        var status = message ?? $"done · {count:N0} · {_stopwatch.ElapsedMilliseconds:N0} ms";
        // A completed indeterminate bar becomes a full determinate one; a
        // determinate bar keeps its real fraction (Done(50) of 100 stays half).
        var total = _total > 0 ? _total : (count > 0 ? count : 1);
        var completed = _total > 0 ? count : total;
        _cell.Update(new DisplayProgress(_label, status, completed, total));
    }
}
