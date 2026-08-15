using System.Diagnostics;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// Live progress as a display concept: create one, <see cref="Show"/> it, call
/// <see cref="Report"/> as work advances and <see cref="Done"/> when finished — each
/// call re-displays the current state through this instance's <see cref="DisplayCell"/>.
/// A non-positive <see cref="Total"/> means indeterminate ("N processed"); the
/// registered formatters draw the bar (ClrKernel.Formatting.Html) and the plain-text
/// form. Available to C# cells directly (e.g. a long loop) and used by SQL bulk copy.
/// </summary>
public sealed class DisplayProgress : IDisplayValue {
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private DisplayCell _cell;

    public DisplayProgress(string label, string status = null, decimal completed = 0, decimal total = 0) {
        Label = label ?? string.Empty;
        Status = status;
        Completed = completed;
        Total = total;
    }

    public string Label { get; private set; }

    /// <summary>Free text shown instead of the computed "N% · a / b" when set.</summary>
    public string Status { get; private set; }

    public decimal Completed { get; private set; }

    public decimal Total { get; private set; }

    public object Value => this;

    /// <summary>Displays the current state (first call creates the output; later
    /// calls are no-ops — Report/Done update it in place).</summary>
    public DisplayProgress Show() {
        if (_cell == null) {
            _cell = this.Display();
        }
        return this;
    }

    /// <summary>Updates the known total (e.g. once a row count is known).</summary>
    public void SetTotal(decimal total) {
        Total = total;
    }

    /// <summary>Reports current progress and re-renders.</summary>
    public void Report(decimal completed, string label = null) {
        if (label != null) {
            Label = label;
        }
        Completed = completed;
        Redisplay();
    }

    /// <summary>Marks the work complete with an optional final message.</summary>
    public void Done(decimal? finalCount = null, string message = null) {
        _stopwatch.Stop();
        var count = finalCount ?? Total;
        Status = message ?? $"done · {count:N0} · {_stopwatch.ElapsedMilliseconds:N0} ms";
        // A completed indeterminate bar becomes a full determinate one; a
        // determinate bar keeps its real fraction (Done(50) of 100 stays half).
        Total = Total > 0 ? Total : (count > 0 ? count : 1);
        Completed = Total > 0 && count <= Total ? count : Total;
        Redisplay();
    }

    private void Redisplay() {
        if (_cell == null) {
            _cell = this.Display();
        } else {
            _cell.Update(this);
        }
    }
}
