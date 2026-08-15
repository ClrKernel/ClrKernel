using ClrKernel.Formatting.Html;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// PowerShell colours its own output, so a cell's text arrives full of ESC[...m sequences. Shown
/// raw they are literal gibberish around every value; this turns them into what the shell meant.
/// </summary>
[TestClass]
public class AnsiRendererTest {
    private const string _esc = "\u001b";

    [TestMethod]
    public void Plain_text_passes_through_untouched() {
        Assert.AreEqual("hello", AnsiRenderer.Strip("hello"));
        Assert.IsFalse(AnsiRenderer.ContainsEscapes("hello"));
        StringAssert.Contains(AnsiRenderer.ToHtml("hello"), ">hello");
    }

    [TestMethod]
    public void Strip_removes_the_sequences_and_keeps_the_words() {
        Assert.AreEqual("error!", AnsiRenderer.Strip($"{_esc}[31merror!{_esc}[0m"));
        // Cursor moves and erases are not styling and must not survive as text either.
        Assert.AreEqual("ab", AnsiRenderer.Strip($"a{_esc}[2Kb"));
    }

    [TestMethod]
    public void A_colour_becomes_a_span_the_theme_can_follow() {
        var html = AnsiRenderer.ToHtml($"{_esc}[31mred{_esc}[0m");
        StringAssert.Contains(html, "--vscode-terminal-ansiRed");
        StringAssert.Contains(html, ">red<");
        Assert.AreEqual(1, Occurrences(html, "<span"), "one span for one colour run");
        Assert.AreEqual(1, Occurrences(html, "</span>"), "and it is closed");
    }

    [TestMethod]
    public void Bright_background_bold_and_underline_all_render() {
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[91mx"), "BrightRed");
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[42mx"), "background:");
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[1mx"), "font-weight:bold");
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[4mx"), "underline");
    }

    [TestMethod]
    public void Extended_colours_are_understood() {
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[38;2;10;20;30mx"), "rgb(10,20,30)");
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[38;5;196mx"), "rgb(");
        StringAssert.Contains(AnsiRenderer.ToHtml($"{_esc}[38;5;1mx"), "ansiRed");
    }

    [TestMethod]
    public void Every_span_is_closed_even_when_a_reset_is_missing() {
        // PowerShell does not always reset before a newline.
        var html = AnsiRenderer.ToHtml($"{_esc}[31mred\nplain");
        Assert.AreEqual(Occurrences(html, "<span"), Occurrences(html, "</span>"),
            "unbalanced markup breaks the rest of the cell");
    }

    [TestMethod]
    public void Html_in_the_output_is_escaped_not_rendered() {
        var html = AnsiRenderer.ToHtml("<script>alert(1)</script> & more");
        StringAssert.Contains(html, "&lt;script&gt;");
        StringAssert.Contains(html, "&amp; more");
        Assert.IsFalse(html.Contains("<script>"));
    }

    [TestMethod]
    public void Trailing_padding_is_trimmed_so_the_cell_does_not_scroll_over_nothing() {
        // Out-String pads every row to its width; in a <pre> that is a wall of invisible spaces.
        StringAssert.Contains(AnsiRenderer.ToHtml("Name" + new string(' ', 100) + "\nvalue"), "Name\n");
    }

    [TestMethod]
    public void Line_structure_survives_including_crlf() {
        StringAssert.Contains(AnsiRenderer.ToHtml("a\r\nb"), "a\nb");
        Assert.AreEqual("a\r\nb", AnsiRenderer.Strip("a\r\nb"), "Strip leaves text it has nothing to remove from");
    }

    [TestMethod]
    public void An_unterminated_escape_does_not_hang_or_leak() {
        Assert.AreEqual("a", AnsiRenderer.Strip($"a{_esc}[31"));
        StringAssert.Contains(AnsiRenderer.ToHtml($"a{_esc}[31"), "a");
    }

    private static int Occurrences(string haystack, string needle) {
        var count = 0;
        for (var i = haystack.IndexOf(needle); i >= 0; i = haystack.IndexOf(needle, i + 1)) {
            count++;
        }
        return count;
    }
}
