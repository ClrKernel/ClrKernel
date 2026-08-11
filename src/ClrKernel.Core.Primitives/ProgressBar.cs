using System;
using System.Diagnostics;

namespace ClrKernel.Core.Primitives {
    /// <summary>
    /// A live, updatable progress bar rendered as self-contained HTML (works in
    /// VS Code notebooks, JupyterLab, and .nb.md previews with no custom renderer).
    /// Create one, call <see cref="Report"/> as work advances, and <see cref="Done"/>
    /// when finished. Backed by <see cref="DisplayedValue"/>, so updates keep
    /// flowing to the originating cell even from background callbacks.
    /// <para>
    /// Available to C# cells directly (e.g. a long loop) and used by SQL bulk copy.
    /// </para>
    /// </summary>
    public sealed class ProgressBar {
        private readonly DisplayedValue _view;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private string _label;
        private long _total;

        /// <summary>Creates and shows a progress bar. A non-positive total renders an
        /// indeterminate "N processed" counter instead of a percentage.</summary>
        public ProgressBar(string label, long total = 0) {
            _label = label ?? string.Empty;
            _total = total;
            _view = Render(0).DisplayAs("text/html");
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
            _view.Update(Render(current));
        }

        /// <summary>Marks the bar complete (100%) with an optional final message.</summary>
        public void Done(long? finalCount = null, string message = null) {
            _stopwatch.Stop();
            var count = finalCount ?? _total;
            _view.Update(Render(count, done: true, message: message));
        }

        private string Render(long current, bool done = false, string message = null) {
            var determinate = _total > 0;
            var pct = determinate ? Math.Min(100.0, current * 100.0 / _total) : (done ? 100.0 : 0.0);
            var barColor = done ? "#1a7f37" : "#0969da";
            var track = "#e6e8eb";
            var width = determinate || done ? pct : 100.0;

            var counter = determinate
                ? $"{current:N0} / {_total:N0}"
                : $"{current:N0}";
            var right = done
                ? (message ?? $"done · {counter} · {_stopwatch.ElapsedMilliseconds:N0} ms")
                : (determinate ? $"{pct:0.#}% · {counter}" : counter);

            // Indeterminate + not done: a subtle striped animation.
            var barStyle = (!determinate && !done)
                ? $"width:100%;background:repeating-linear-gradient(45deg,{barColor},{barColor} 10px,#4c9aff 10px,#4c9aff 20px)"
                : $"width:{width:0.##}%;background:{barColor}";

            return
                "<div style=\"font:12px/1.4 -apple-system,Segoe UI,sans-serif;max-width:520px\">" +
                "<div style=\"display:flex;justify-content:space-between;margin-bottom:3px;color:#57606a\">" +
                $"<span>{Encode(_label)}</span><span>{Encode(right)}</span></div>" +
                $"<div style=\"height:8px;border-radius:5px;background:{track};overflow:hidden\">" +
                $"<div style=\"height:100%;border-radius:5px;{barStyle};transition:width .2s\"></div>" +
                "</div></div>";
        }

        private static string Encode(string s) =>
            System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
    }
}
