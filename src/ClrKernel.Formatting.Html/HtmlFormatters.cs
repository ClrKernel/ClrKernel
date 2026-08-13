using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Formatting.Html;

/// <summary>
/// Registers this package's renders with the <see cref="DisplayFormatters"/> registry.
/// Called once by the kernel's composition root (and the test mirrors), the same
/// pattern as <c>CellLanguages.RegisterDefaults()</c>. Every registration is an
/// ordinary formatter, so a user (or another package) overrides any of them by
/// registering their own afterwards.
/// </summary>
public static class HtmlFormatters {
    private static readonly object _gate = new object();
    private static IReadOnlyList<DisplayFormatter> _registered;

    public static IReadOnlyList<DisplayFormatter> RegisterDefaults() {
        lock (_gate) {
            if (_registered != null) {
                return _registered;
            }
            _registered = new[] {
                // Arbitrary objects: the rich render (property tables, sequences,
                // type badge) that trailing cell values have always had.
                // ResultFormatter still lives in Primitives until every caller is
                // off it; its implementation moves here at the end of HANDOFF-18.
                DisplayFormatters.Register<DisplayObject, DisplayText>(o =>
                    new DisplayText((string)ResultFormatter.Format(o.Value).Data["text/plain"])),
                DisplayFormatters.Register<DisplayObject, DisplayHtml>(o =>
                    new DisplayHtml((string)ResultFormatter.Format(o.Value).Data["text/html"])),
                DisplayFormatters.Register<DisplayObject, DisplayTable>(TableExtractor.Extract),

                DisplayFormatters.Register<DisplayConsoleText, DisplayText>(c =>
                    new DisplayText(AnsiRenderer.Strip(c.ConsoleOutput ?? ""))),
                DisplayFormatters.Register<DisplayConsoleText, DisplayHtml>(c =>
                    new DisplayHtml(AnsiRenderer.ToHtml(c.ConsoleOutput ?? ""))),

                DisplayFormatters.Register<DisplayTable, DisplayText>(TableText),
                DisplayFormatters.Register<DisplayTable, DisplayHtml>(t =>
                    new DisplayHtml(InteractiveTable.Render(
                        t.Columns,
                        t.Rows,
                        t.Types ?? t.Columns.Select(_ => InteractiveTable.Text).ToArray(),
                        t.TotalRows ?? t.Rows.Count))),

                DisplayFormatters.Register<DisplayProgress, DisplayHtml>(p =>
                    new DisplayHtml(ProgressHtml.Render(p))),
            };
            return _registered;
        }
    }

    /// <summary>Removes every default registration (test isolation).</summary>
    public static void UnregisterDefaults() {
        lock (_gate) {
            if (_registered == null) {
                return;
            }
            foreach (var formatter in _registered) {
                DisplayFormatters.Unregister(formatter);
            }
            _registered = null;
        }
    }

    private const int _textRowLimit = 50;

    private static DisplayText TableText(DisplayTable table) {
        var lines = new List<string> { string.Join("\t", table.Columns) };
        foreach (var row in table.Rows.Take(_textRowLimit)) {
            lines.Add(string.Join("\t", row));
        }
        var total = table.TotalRows ?? table.Rows.Count;
        if (table.Rows.Count > _textRowLimit || total > table.Rows.Count) {
            lines.Add(total >= 0 ? $"… ({total:N0} rows)" : "…");
        }
        return new DisplayText(string.Join("\n", lines));
    }
}
