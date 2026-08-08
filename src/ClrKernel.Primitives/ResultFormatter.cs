using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace ClrKernel.Primitives {
    /// <summary>
    /// Turns a bare cell result into a rich <see cref="DisplayData"/> (text/html
    /// with a text/plain fallback) so notebook output reads well without calling
    /// the Display helpers: sequences render as HTML tables, objects without a
    /// meaningful ToString render as property tables, and everything carries a
    /// small type-hint badge (hover for the full type name, click to expand
    /// details). Anonymous types and records keep their clean <c>{ x = 10 }</c>
    /// form instead of Roslyn's <c>&lt;&gt;f__AnonymousType…</c> noise.
    /// </summary>
    public static class ResultFormatter {
        private const int _rowLimit = 100;

        public static DisplayData Format(object value) {
            if (value is null) {
                return new DisplayData("null");
            }

            var type = value.GetType();
            string plain;
            string valueHtml;
            string extra = null;

            if (value is string str) {
                plain = str;
                valueHtml = "<span>" + Encode(str) + "</span>";
            } else if (IsScalar(type)) {
                plain = Convert.ToString(value, CultureInfo.InvariantCulture);
                valueHtml = "<span>" + Encode(plain) + "</span>";
            } else if (value is IEnumerable enumerable) {
                valueHtml = RenderTable(enumerable, out var count, out plain);
                extra = count;
            } else if (HasMeaningfulToString(type)) {
                plain = value.ToString();
                valueHtml = "<span>" + Encode(plain) + "</span>";
            } else {
                valueHtml = RenderObject(value, type, out plain);
            }

            var dd = new DisplayData();
            dd.Data["text/html"] = Wrap(type, extra, valueHtml);
            dd.Data["text/plain"] = plain ?? string.Empty;
            return dd;
        }

        // --- value renderers ---------------------------------------------------

        private static string RenderObject(object value, Type type, out string plain) {
            var properties = ReadableProperties(type);
            if (properties.Length == 0) {
                plain = value.ToString();
                return "<span>" + Encode(plain) + "</span>";
            }

            var html = new StringBuilder("<table><thead><tr><th>Property</th><th>Value</th></tr></thead><tbody>");
            var parts = new List<string>();
            foreach (var property in properties) {
                var cell = SafeGet(property, value);
                html.Append("<tr><td>").Append(Encode(property.Name))
                    .Append("</td><td>").Append(Encode(cell)).Append("</td></tr>");
                parts.Add(property.Name + " = " + cell);
            }
            html.Append("</tbody></table>");
            plain = "{ " + string.Join(", ", parts) + " }";
            return html.ToString();
        }

        private static string RenderTable(IEnumerable source, out string count, out string plain) {
            var items = new List<object>();
            var truncated = false;
            foreach (var item in source) {
                if (items.Count >= _rowLimit) {
                    truncated = true;
                    break;
                }
                items.Add(item);
            }
            count = items.Count + (truncated ? "+" : string.Empty) + " item" + (items.Count == 1 && !truncated ? string.Empty : "s");

            // Dictionary rows (name -> value): columns are the union of keys.
            if (items.Count > 0 && items[0] is IDictionary<string, object>) {
                return RenderDictionaryRows(items, truncated, out plain);
            }

            var elementType = items.FirstOrDefault(i => i != null)?.GetType();
            var properties = elementType != null && !IsScalar(elementType) && elementType != typeof(string)
                ? ReadableProperties(elementType)
                : Array.Empty<PropertyInfo>();

            var html = new StringBuilder("<table><thead><tr>");
            if (properties.Length == 0) {
                html.Append("<th>Value</th>");
            } else {
                foreach (var property in properties) {
                    html.Append("<th>").Append(Encode(property.Name)).Append("</th>");
                }
            }
            html.Append("</tr></thead><tbody>");

            var preview = new List<string>();
            foreach (var item in items) {
                html.Append("<tr>");
                if (properties.Length == 0) {
                    var text = ToText(item);
                    html.Append("<td>").Append(Encode(text)).Append("</td>");
                    preview.Add(text);
                } else {
                    foreach (var property in properties) {
                        html.Append("<td>").Append(Encode(SafeGet(property, item))).Append("</td>");
                    }
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            if (truncated) {
                html.Append("<div><i>(showing first ").Append(_rowLimit).Append(" rows)</i></div>");
            }

            plain = properties.Length == 0
                ? "[" + string.Join(", ", preview.Take(10)) + (preview.Count > 10 || truncated ? ", …" : string.Empty) + "]"
                : FriendlyName(elementType ?? typeof(object)) + " × " + count;
            return html.ToString();
        }

        private static string RenderDictionaryRows(List<object> items, bool truncated, out string plain) {
            var columns = new List<string>();
            foreach (IDictionary<string, object> row in items) {
                foreach (var key in row.Keys) {
                    if (!columns.Contains(key)) {
                        columns.Add(key);
                    }
                }
            }

            var html = new StringBuilder("<table><thead><tr>");
            foreach (var column in columns) {
                html.Append("<th>").Append(Encode(column)).Append("</th>");
            }
            html.Append("</tr></thead><tbody>");
            foreach (IDictionary<string, object> row in items) {
                html.Append("<tr>");
                foreach (var column in columns) {
                    row.TryGetValue(column, out var cell);
                    html.Append("<td>").Append(Encode(ToText(cell))).Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            if (truncated) {
                html.Append("<div><i>(showing first ").Append(_rowLimit).Append(" rows)</i></div>");
            }
            plain = items.Count + " row" + (items.Count == 1 ? string.Empty : "s");
            return html.ToString();
        }

        // --- type badge --------------------------------------------------------

        private static string Wrap(Type type, string extra, string valueHtml) {
            var friendly = FriendlyName(type);
            var full = FriendlyName(type, true);
            var badge = "ⓘ " + friendly + (extra != null ? " — " + extra : string.Empty);
            var detail = "Type: " + Encode(full);

            var summaryStyle = "cursor:pointer;list-style:none;color:var(--vscode-descriptionForeground,#888);" +
                "font-family:var(--vscode-editor-font-family,monospace);font-size:11px";
            var detailStyle = "color:var(--vscode-descriptionForeground,#888);" +
                "font-family:var(--vscode-editor-font-family,monospace);font-size:11px;padding:2px 0 0 16px";

            return "<div class=\"clrkernel-result\">" +
                "<div class=\"clrkernel-value\">" + valueHtml + "</div>" +
                "<details style=\"margin-top:3px\"><summary style=\"" + summaryStyle + "\" title=\"" + Encode(full) + "\">" +
                Encode(badge) + "</summary><div style=\"" + detailStyle + "\">" + detail + "</div></details>" +
                "</div>";
        }

        // --- helpers -----------------------------------------------------------

        private static bool IsScalar(Type type) =>
            type.IsPrimitive || type.IsEnum
            || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(Guid);

        private static bool HasMeaningfulToString(Type type) {
            var method = type.GetMethod("ToString", Type.EmptyTypes);
            var declaring = method?.DeclaringType;
            return declaring != null && declaring != typeof(object) && declaring != typeof(ValueType);
        }

        private static PropertyInfo[] ReadableProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray();

        private static string SafeGet(PropertyInfo property, object target) {
            try {
                return ToText(property.GetValue(target));
            } catch (Exception e) {
                return "<error: " + e.GetBaseException().Message + ">";
            }
        }

        private static string ToText(object value) {
            if (value == null) {
                return string.Empty;
            }
            if (value is string s) {
                return s;
            }
            if (value is IFormattable formattable) {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static readonly Dictionary<Type, string> _keywords = new Dictionary<Type, string> {
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(char)] = "char",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(float)] = "float",
            [typeof(double)] = "double",
            [typeof(decimal)] = "decimal",
            [typeof(string)] = "string",
            [typeof(object)] = "object",
        };

        /// <summary>A readable C#-style type name (int, List&lt;int&gt;, int[], anonymous).</summary>
        internal static string FriendlyName(Type type) => FriendlyName(type, false);

        /// <summary>
        /// Readable type name. When <paramref name="qualified"/>, namespaces are
        /// included and anonymous types expand to their property shape — used for
        /// the type-hint tooltip/details.
        /// </summary>
        internal static string FriendlyName(Type type, bool qualified) {
            if (type == null) {
                return "object";
            }
            if (_keywords.TryGetValue(type, out var keyword)) {
                return keyword;
            }
            if (type.IsArray) {
                return FriendlyName(type.GetElementType(), qualified) + "[]";
            }
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) {
                return FriendlyName(underlying, qualified) + "?";
            }
            if (IsAnonymous(type)) {
                if (!qualified) {
                    return "anonymous";
                }
                var shape = ReadableProperties(type).Select(p => p.Name + ": " + FriendlyName(p.PropertyType));
                return "anonymous { " + string.Join(", ", shape) + " }";
            }
            var prefix = qualified && !string.IsNullOrEmpty(type.Namespace) ? type.Namespace + "." : string.Empty;
            if (type.IsGenericType) {
                var name = type.Name;
                var tick = name.IndexOf('`');
                if (tick >= 0) {
                    name = name.Substring(0, tick);
                }
                var args = type.GetGenericArguments().Select(a => FriendlyName(a, qualified));
                return prefix + name + "<" + string.Join(", ", args) + ">";
            }
            return prefix + type.Name;
        }

        private static bool IsAnonymous(Type type) =>
            type.IsClass
            && type.IsSealed
            && type.Namespace == null
            && (type.Name.StartsWith("<>", StringComparison.Ordinal) || type.Name.Contains("AnonymousType"));
    }
}
