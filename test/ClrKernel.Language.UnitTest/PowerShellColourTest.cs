using System;
using ClrKernel.Language.PowerShell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// PowerShell only colours its output when it believes the host can show it. A hosted runspace is
/// not a console, so <c>$PSStyle.OutputRendering</c> defaults to <c>Host</c> and every table,
/// warning and error came through with the colour never produced — not stripped later, never
/// emitted at all. A renderer that turns escape sequences into HTML is no use with nothing to
/// render, which is why the output stayed uniformly grey.
/// </summary>
[TestClass]
public class PowerShellColourTest {
    private static string Html(PowerShellSession session, string code) =>
        session.Execute(code).Data.TryGetValue("text/html", out var h) ? h?.ToString() ?? string.Empty : string.Empty;

    private static string Plain(PowerShellSession session, string code) =>
        session.Execute(code).Data["text/plain"].ToString();

    private static int Spans(string html) =>
        html.Split(new[] { "<span" }, StringSplitOptions.None).Length - 1;

    [TestMethod]
    public void The_runspace_is_told_to_emit_ansi() {
        StringAssert.Contains(Plain(new PowerShellSession(), "$PSStyle.OutputRendering"), "Ansi");
    }

    [TestMethod]
    public void Formatted_output_arrives_coloured() {
        // The common case: Get-*, Format-Table, anything with a view.
        Assert.IsTrue(Spans(Html(new PowerShellSession(), "Get-Item . | Format-Table -AutoSize | Out-String")) > 0,
            "table formatting should carry colour");
    }

    [TestMethod]
    public void Write_Host_colours_survive() {
        var session = new PowerShellSession();
        // Write-Host carries colour as properties on a HostInformationMessage rather than as
        // escapes — a console host applies them itself — so they were being dropped on the floor.
        StringAssert.Contains(Html(session, "Write-Host 'hi' -ForegroundColor Red"), "ansiBrightRed");
        StringAssert.Contains(Html(session, "Write-Host 'hi' -ForegroundColor DarkGreen"), "ansiGreen");
        // A background only reaches us alongside a foreground: Write-Host with -BackgroundColor
        // alone delivers a HostInformationMessage with BOTH colours null — PowerShell drops it
        // before our code runs (probed against the live runspace, not assumed).
        StringAssert.Contains(Html(session, "Write-Host 'hi' -ForegroundColor White -BackgroundColor Blue"), "background:");
    }

    [TestMethod]
    public void Warnings_are_yellow_and_errors_red_as_a_console_shows_them() {
        var session = new PowerShellSession();
        StringAssert.Contains(Html(session, "Write-Warning 'careful'"), "ansiBrightYellow");
        StringAssert.Contains(Html(session, "Write-Error 'boom'"), "ansiBrightRed");
    }

    [TestMethod]
    public void The_plain_text_view_stays_free_of_escape_sequences() {
        // Jupyter and headless runs read text/plain, where an escape is literal gibberish.
        var plain = Plain(new PowerShellSession(), "Write-Host 'hi' -ForegroundColor Red");
        Assert.AreEqual("hi", plain);
        Assert.IsFalse(plain.Contains('\u001b'));
    }

    [TestMethod]
    public void Output_with_no_colour_gains_no_markup() {
        Assert.AreEqual(0, Spans(Html(new PowerShellSession(), "'plain text'")), "nothing to colour, nothing added");
    }
}
