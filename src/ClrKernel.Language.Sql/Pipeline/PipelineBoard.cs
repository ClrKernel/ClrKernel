using System.Collections.Generic;
using System.Net;
using System.Text;

namespace ClrKernel.Language.Sql;
/// <summary>Renders a pipeline run's step states as a self-contained HTML board.</summary>
public static class PipelineBoard {
    public static string Render(IReadOnlyList<StepStatus> steps) {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font:12px/1.5 -apple-system,Segoe UI,sans-serif\">");
        sb.Append("<table style=\"border-collapse:collapse;min-width:420px\">");
        sb.Append("<thead><tr style=\"text-align:left;color:#57606a;border-bottom:1px solid #d0d7de\">" +
                  "<th style=\"padding:3px 10px 3px 0\">Step</th><th style=\"padding:3px 10px\">Status</th>" +
                  "<th style=\"padding:3px 10px\">Time</th><th style=\"padding:3px 0\">Detail</th></tr></thead><tbody>");
        foreach (var s in steps) {
            var (icon, color) = Badge(s.State);
            var time = s.Outcome != null && (s.State == StepState.Done || s.State == StepState.Failed)
                ? $"{s.Outcome.ElapsedMs:N0} ms" : "";
            var detail = s.State == StepState.Failed ? (s.Outcome?.Error ?? "") : (s.Outcome?.Message ?? "");
            sb.Append("<tr style=\"border-bottom:1px solid #f0f2f4\">");
            sb.Append($"<td style=\"padding:3px 10px 3px 0;font-weight:600\">{Enc(s.Step.Name)}</td>");
            sb.Append($"<td style=\"padding:3px 10px;color:{color}\">{icon} {s.State}</td>");
            sb.Append($"<td style=\"padding:3px 10px;color:#57606a\">{time}</td>");
            sb.Append($"<td style=\"padding:3px 0;color:#57606a\">{Enc(Truncate(detail, 80))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    private static (string icon, string color) Badge(StepState state) => state switch {
        StepState.Done => ("●", "#1a7f37"),
        StepState.Running => ("◐", "#0969da"),
        StepState.Failed => ("✕", "#cf222e"),
        StepState.Skipped => ("○", "#8c959f"),
        _ => ("○", "#8c959f"),
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static string Enc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
