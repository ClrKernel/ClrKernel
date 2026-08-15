using System;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Formatting.Html;

/// <summary>
/// Renders the <see cref="DisplayProgress"/> concept as the same self-contained bar
/// the old <c>ProgressBar</c> drew: determinate when a positive total is known,
/// striped-indeterminate otherwise, green at 100%.
/// </summary>
public static class ProgressHtml {
    public static string Render(DisplayProgress progress) {
        var determinate = progress.Total > 0;
        var pct = determinate ? Math.Min(100.0m, progress.Completed * 100.0m / progress.Total) : 0.0m;
        var done = determinate && pct >= 100.0m;
        var barColor = done ? "#1a7f37" : "#0969da";
        var track = "#e6e8eb";

        var right = progress.Status;
        if (string.IsNullOrEmpty(right)) {
            right = determinate
                ? $"{pct:0.#}% · {progress.Completed:N0} / {progress.Total:N0}"
                : $"{progress.Completed:N0}";
        }

        // Indeterminate: a subtle striped animation instead of a fill width.
        var barStyle = determinate
            ? $"width:{pct:0.##}%;background:{barColor}"
            : $"width:100%;background:repeating-linear-gradient(45deg,{barColor},{barColor} 10px,#4c9aff 10px,#4c9aff 20px)";

        return
            "<div style=\"font:12px/1.4 -apple-system,Segoe UI,sans-serif;max-width:520px\">" +
            "<div style=\"display:flex;justify-content:space-between;margin-bottom:3px;color:#57606a\">" +
            $"<span>{Encode(progress.Label)}</span><span>{Encode(right)}</span></div>" +
            $"<div style=\"height:8px;border-radius:5px;background:{track};overflow:hidden\">" +
            $"<div style=\"height:100%;border-radius:5px;{barStyle};transition:width .2s\"></div>" +
            "</div></div>";
    }

    private static string Encode(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
