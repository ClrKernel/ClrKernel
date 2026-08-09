using System.Collections.Generic;
using System.Net;
using System.Text;

namespace ClrKernel.Sql.Deploy;
/// <summary>Renders a deployment's per-file state as self-contained HTML.</summary>
public static class DeployBoard {
    public static string Render(IReadOnlyList<DeployFileResult> files, bool dryRun) {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font:12px/1.5 -apple-system,Segoe UI,sans-serif\">");
        if (dryRun) {
            sb.Append("<div style=\"color:#9a6700;margin-bottom:4px\">dry run — nothing executed</div>");
        }
        sb.Append("<table style=\"border-collapse:collapse;min-width:420px\">");
        sb.Append("<thead><tr style=\"text-align:left;color:#57606a;border-bottom:1px solid #d0d7de\">" +
                  "<th style=\"padding:3px 10px 3px 0\">File</th><th style=\"padding:3px 10px\">Status</th>" +
                  "<th style=\"padding:3px 10px\">Batches</th><th style=\"padding:3px 0\">Detail</th></tr></thead><tbody>");
        foreach (var f in files) {
            var (icon, color) = Badge(f.State);
            var label = f.State == DeployState.Deployed && f.Pass > 1 ? $"{f.State} (pass {f.Pass})" : f.State.ToString();
            var detail = f.State == DeployState.Failed ? (f.Error ?? "") : "";
            sb.Append("<tr style=\"border-bottom:1px solid #f0f2f4\">");
            sb.Append($"<td style=\"padding:3px 10px 3px 0;font-weight:600\">{Enc(f.Name)}</td>");
            sb.Append($"<td style=\"padding:3px 10px;color:{color}\">{icon} {Enc(label)}</td>");
            sb.Append($"<td style=\"padding:3px 10px;color:#57606a\">{f.Batches}</td>");
            sb.Append($"<td style=\"padding:3px 0;color:#57606a\">{Enc(Truncate(detail, 80))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    private static (string, string) Badge(DeployState state) => state switch {
        DeployState.Deployed => ("●", "#1a7f37"),
        DeployState.Failed => ("✕", "#cf222e"),
        _ => ("○", "#8c959f"),
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static string Enc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
