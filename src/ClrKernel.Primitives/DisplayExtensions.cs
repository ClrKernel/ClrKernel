using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace ClrKernel.Primitives {
    /// <summary>
    /// Display helpers available in notebook cells via the ClrKernel.Primitives
    /// namespace import. Kept separate from <see cref="DisplayDataEmitter"/> (which
    /// the kernel imports with <c>using static</c>) so extension-method resolution
    /// stays unambiguous.
    /// </summary>
    public static class DisplayExtensions {
        /// <summary>
        /// Displays content and returns a handle that can update it in place
        /// (e.g. <c>var dv = "".DisplayAs("text/html"); dv.Update(html);</c>).
        /// </summary>
        public static DisplayedValue DisplayAs(this string content, string mimeType) {
            var displayId = Guid.NewGuid().ToString("N");
            var data = new DisplayData();
            data.Data[mimeType] = content ?? "";
            data.Transient["display_id"] = displayId;
            DisplayDataEmitter.Emit(data);

            // Capture the current update handler: it is bound to the executing
            // cell's parent message, so later background updates still publish
            // against the right output even after the cell completes.
            var update = DisplayDataEmitter.UpdateDisplayDataHandler;
            return new DisplayedValue(displayId, mimeType, d => update?.Invoke(d));
        }

        /// <summary>
        /// Displays a value (ToString form) and returns an updatable handle.
        /// Mirrors .NET Interactive's object.Display() for migrated notebooks.
        /// </summary>
        public static DisplayedValue Display(this object value, string mimeType = "text/plain") {
            return (value?.ToString() ?? "").DisplayAs(mimeType);
        }

        /// <summary>
        /// Renders dictionary rows (column name → value, e.g. data-reader
        /// previews) as an HTML table; columns come from the keys in order of
        /// first appearance.
        /// </summary>
        public static DisplayedValue DisplayTable(this IEnumerable<IDictionary<string, object>> rows, int limit = 100) {
            var taken = rows.Take(limit + 1).ToList();
            var truncated = taken.Count > limit;
            if (truncated) {
                taken.RemoveAt(taken.Count - 1);
            }

            var columns = new List<string>();
            foreach (var row in taken) {
                foreach (var key in row.Keys) {
                    if (!columns.Contains(key)) {
                        columns.Add(key);
                    }
                }
            }

            var html = new StringBuilder("<table><thead><tr>");
            foreach (var column in columns) {
                html.Append("<th>").Append(WebUtility.HtmlEncode(column)).Append("</th>");
            }
            html.Append("</tr></thead><tbody>");
            foreach (var row in taken) {
                html.Append("<tr>");
                foreach (var column in columns) {
                    row.TryGetValue(column, out var cell);
                    html.Append("<td>").Append(WebUtility.HtmlEncode(cell?.ToString() ?? "")).Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            if (truncated) {
                html.Append("<i>(showing first ").Append(limit).Append(" rows)</i>");
            }

            return html.ToString().DisplayAs("text/html");
        }

        /// <summary>
        /// Renders a sequence as an HTML table (columns from the element type's
        /// public properties; scalar sequences get a single Value column) and
        /// returns an updatable handle. Mirrors .NET Interactive's DisplayTable().
        /// </summary>
        public static DisplayedValue DisplayTable<T>(this IEnumerable<T> source, int limit = 100) {
            // Sequences of dictionary rows render by key, not by Dictionary's own
            // properties (overload resolution prefers this generic method for
            // List<Dictionary<...>>, so re-dispatch at runtime).
            if (source is IEnumerable<IDictionary<string, object>> dictionaryRows) {
                return DisplayTable(dictionaryRows, limit);
            }

            var rows = source.Take(limit + 1).ToList();
            var truncated = rows.Count > limit;
            if (truncated) {
                rows.RemoveAt(rows.Count - 1);
            }

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray();

            var html = new StringBuilder("<table><thead><tr>");
            if (properties.Length == 0) {
                html.Append("<th>Value</th>");
            } else {
                foreach (var property in properties) {
                    html.Append("<th>").Append(WebUtility.HtmlEncode(property.Name)).Append("</th>");
                }
            }
            html.Append("</tr></thead><tbody>");

            foreach (var row in rows) {
                html.Append("<tr>");
                if (properties.Length == 0) {
                    html.Append("<td>").Append(WebUtility.HtmlEncode(row?.ToString() ?? "")).Append("</td>");
                } else {
                    foreach (var property in properties) {
                        object cell;
                        try {
                            cell = property.GetValue(row);
                        } catch (Exception e) {
                            cell = $"<error: {e.GetBaseException().Message}>";
                        }
                        html.Append("<td>").Append(WebUtility.HtmlEncode(cell?.ToString() ?? "")).Append("</td>");
                    }
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            if (truncated) {
                html.Append("<i>(showing first ").Append(limit).Append(" rows)</i>");
            }

            return html.ToString().DisplayAs("text/html");
        }
    }
}
