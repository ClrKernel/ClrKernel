using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClrKernel.Core.Primitives {

    /// <summary>
    /// Turns console output containing ANSI escape sequences into HTML, and strips it for plain text.
    /// <para>
    /// PowerShell 7 colours its own output (<c>PSStyle</c>, <c>Write-Host -ForegroundColor</c>, error
    /// records), so a cell's text arrives full of <c>ESC[…m</c> sequences. Rendered as plain text those
    /// show up as literal escape gibberish around every value; stripped, the output is readable but
    /// flat. Rendering them keeps the colour the shell intended.
    /// </para>
    /// </summary>
    public static class AnsiRenderer {
        private const char _escape = '\u001b';

        /// <summary>
        /// The 16 basic colours, as VS Code's terminal theme variables so they follow the user's
        /// theme, each with a fallback for hosts that don't define them (Jupyter, a saved HTML export).
        /// </summary>
        private static readonly string[] _basic = {
            "var(--vscode-terminal-ansiBlack, #000000)",
            "var(--vscode-terminal-ansiRed, #cd3131)",
            "var(--vscode-terminal-ansiGreen, #0dbc79)",
            "var(--vscode-terminal-ansiYellow, #e5e510)",
            "var(--vscode-terminal-ansiBlue, #2472c8)",
            "var(--vscode-terminal-ansiMagenta, #bc3fbc)",
            "var(--vscode-terminal-ansiCyan, #11a8cd)",
            "var(--vscode-terminal-ansiWhite, #e5e5e5)",
            "var(--vscode-terminal-ansiBrightBlack, #666666)",
            "var(--vscode-terminal-ansiBrightRed, #f14c4c)",
            "var(--vscode-terminal-ansiBrightGreen, #23d18b)",
            "var(--vscode-terminal-ansiBrightYellow, #f5f543)",
            "var(--vscode-terminal-ansiBrightBlue, #3b8eea)",
            "var(--vscode-terminal-ansiBrightMagenta, #d670d6)",
            "var(--vscode-terminal-ansiBrightCyan, #29b8db)",
            "var(--vscode-terminal-ansiBrightWhite, #ffffff)",
        };

        /// <summary>True when the text contains any escape sequence worth rendering.</summary>
        public static bool ContainsEscapes(string text) => !string.IsNullOrEmpty(text) && text.IndexOf(_escape) >= 0;

        /// <summary>The text with every escape sequence removed.</summary>
        public static string Strip(string text) {
            if (!ContainsEscapes(text)) {
                return text ?? string.Empty;
            }
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++) {
                if (text[i] == _escape) {
                    i = SkipSequence(text, i);
                    continue;
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Renders the text as a self-contained <c>&lt;pre&gt;</c> block: escape sequences become
        /// spans, everything else is HTML-escaped.
        /// </summary>
        /// <remarks>
        /// Trailing spaces are trimmed per line. PowerShell formats through <c>Out-String -Width</c>,
        /// which pads every row to the full width; in a <c>&lt;pre&gt;</c> that becomes a block of
        /// invisible padding wide enough to force a horizontal scrollbar over nothing.
        /// </remarks>
        public static string ToHtml(string text) {
            var sb = new StringBuilder();
            sb.Append("<pre style=\"margin:0;padding:2px 0;font:12px/1.4 ")
              .Append("var(--vscode-editor-font-family,ui-monospace,SFMono-Regular,Menlo,Consolas,monospace);")
              .Append("white-space:pre-wrap;word-break:break-word\">");

            var open = 0;
            foreach (var line in SplitLines(text ?? string.Empty)) {
                var trimmed = line.TrimEnd(' ', '\t');
                for (var i = 0; i < trimmed.Length; i++) {
                    var c = trimmed[i];
                    if (c == _escape) {
                        var end = SkipSequence(trimmed, i);
                        if (IsSgr(trimmed, i, end)) {
                            // A style change closes what was open and opens the new one, so spans
                            // never straddle a line and the markup stays balanced.
                            var style = StyleFor(trimmed.Substring(i + 2, end - i - 2));
                            while (open > 0) {
                                sb.Append("</span>");
                                open--;
                            }
                            if (style != null) {
                                sb.Append("<span style=\"").Append(style).Append("\">");
                                open++;
                            }
                        }
                        i = end;
                        continue;
                    }
                    Encode(sb, c);
                }
                while (open > 0) {
                    sb.Append("</span>");
                    open--;
                }
                sb.Append('\n');
            }

            // The final newline is the separator after the last line, not part of it.
            if (sb[sb.Length - 1] == '\n') {
                sb.Length--;
            }
            return sb.Append("</pre>").ToString();
        }

        private static IEnumerable<string> SplitLines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Returns the index of the sequence's final byte, or the last index if it never terminates.
        private static int SkipSequence(string text, int start) {
            if (start + 1 >= text.Length) {
                return start;
            }
            if (text[start + 1] != '[') {
                return start + 1; // a two-character escape
            }
            for (var i = start + 2; i < text.Length; i++) {
                // CSI runs until a byte in @-~; 'm' is the one that sets graphics.
                if (text[i] >= '@' && text[i] <= '~') {
                    return i;
                }
            }
            return text.Length - 1;
        }

        private static bool IsSgr(string text, int start, int end) =>
            end > start + 1 && text[start + 1] == '[' && text[end] == 'm';

        /// <summary>CSS for an SGR parameter list, or null to close styling (reset).</summary>
        private static string StyleFor(string parameters) {
            var codes = parameters.Split(';');
            var css = new StringBuilder();
            for (var i = 0; i < codes.Length; i++) {
                if (!int.TryParse(codes[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) {
                    continue;
                }
                if (code == 0) {
                    return null;   // reset
                } else if (code == 1) {
                    css.Append("font-weight:bold;");
                } else if (code == 3) {
                    css.Append("font-style:italic;");
                } else if (code == 4) {
                    css.Append("text-decoration:underline;");
                } else if (code == 7) {
                    css.Append("filter:invert(1);");
                } else if (code >= 30 && code <= 37) {
                    css.Append("color:").Append(_basic[code - 30]).Append(';');
                } else if (code >= 90 && code <= 97) {
                    css.Append("color:").Append(_basic[code - 90 + 8]).Append(';');
                } else if (code >= 40 && code <= 47) {
                    css.Append("background:").Append(_basic[code - 40]).Append(';');
                } else if (code >= 100 && code <= 107) {
                    css.Append("background:").Append(_basic[code - 100 + 8]).Append(';');
                } else if (code == 38 || code == 48) {
                    var property = code == 38 ? "color:" : "background:";
                    var colour = ExtendedColour(codes, ref i);
                    if (colour != null) {
                        css.Append(property).Append(colour).Append(';');
                    }
                }
            }
            return css.Length == 0 ? null : css.ToString();
        }

        // 38/48 take their colour from following parameters: ";5;N" for the 256-colour cube,
        // ";2;R;G;B" for truecolor. Advances past whatever it consumed.
        private static string ExtendedColour(string[] codes, ref int i) {
            if (i + 1 >= codes.Length) {
                return null;
            }
            var mode = codes[++i];
            if (mode == "5" && i + 1 < codes.Length && int.TryParse(codes[++i], out var index)) {
                return Colour256(index);
            }
            if (mode == "2" && i + 3 < codes.Length
                && int.TryParse(codes[i + 1], out var r) && int.TryParse(codes[i + 2], out var g)
                && int.TryParse(codes[i + 3], out var b)) {
                i += 3;
                return $"rgb({r},{g},{b})";
            }
            return null;
        }

        private static string Colour256(int index) {
            if (index < 16) {
                return _basic[index];
            }
            if (index < 232) {
                // 6x6x6 cube: the levels are not evenly spaced — 0 then 95, and 40 apart after that.
                var n = index - 16;
                return $"rgb({Level(n / 36)},{Level(n / 6 % 6)},{Level(n % 6)})";
            }
            var grey = 8 + ((index - 232) * 10);
            return $"rgb({grey},{grey},{grey})";
        }

        private static int Level(int step) => step == 0 ? 0 : 55 + (step * 40);

        private static void Encode(StringBuilder sb, char c) {
            switch (c) {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
