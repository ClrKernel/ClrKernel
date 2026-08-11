using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.PowerShell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class PowerShellTest {
    [TestMethod]
    public void Executes_and_formats_output() {
        using var session = new PowerShellSession();
        var text = (string)session.Execute("$x = 21; $x * 2").Data["text/plain"];
        Assert.AreEqual("42", text.Trim());
    }

    [TestMethod]
    public void State_persists_across_cells() {
        using var session = new PowerShellSession();
        session.Execute("$greeting = 'hello'");
        var text = (string)session.Execute("\"$greeting world\"").Data["text/plain"];
        Assert.AreEqual("hello world", text.Trim());
    }

    [TestMethod]
    public void Write_host_is_captured() {
        using var session = new PowerShellSession();
        var text = (string)session.Execute("Write-Host 'from write-host'").Data["text/plain"];
        StringAssert.Contains(text, "from write-host");
    }

    [TestMethod]
    public void Terminating_error_throws() {
        using var session = new PowerShellSession();
        var threw = false;
        try {
            session.Execute("throw 'boom'");
        } catch (PowerShellCellException) {
            threw = true;
        }
        Assert.IsTrue(threw, "a terminating error should throw PowerShellCellException");
    }

    [TestMethod]
    public void Completion_offers_cmdlets() {
        using var session = new PowerShellSession();
        var completion = session.Complete("Get-ChildIt", 11);
        Assert.IsTrue(completion.Items.Any(i => i.InsertText == "Get-ChildItem"),
            "expected Get-ChildItem among completions");
    }

    [TestMethod]
    public void Completion_reflects_session_variables() {
        using var session = new PowerShellSession();
        session.Execute("$myConnectionString = 'x'");
        var completion = session.Complete("$myConn", 7);
        Assert.IsTrue(completion.Items.Any(i => string.Equals(i.InsertText, "$myConnectionString")),
            "session variable should be offered");
    }

    [TestMethod]
    public async Task Engine_routes_pwsh_selector_to_runspace() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var result = await engine.ExecuteAsync("#!pwsh\n2 + 3");
        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "pwsh cell should return display data");
        Assert.AreEqual("5", ((string)dd.Data["text/plain"]).Trim());
    }

    [TestMethod]
    public async Task Engine_accepts_powershell_selector_alias() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var result = await engine.ExecuteAsync("#!powershell\n'hi'.ToUpper()");
        var dd = result as DisplayData;
        Assert.IsNotNull(dd);
        Assert.AreEqual("HI", ((string)dd.Data["text/plain"]).Trim());
    }

    [TestMethod]
    public void Hover_on_cmdlet_describes_the_command() {
        using var session = new PowerShellSession();
        const string code = "Get-ChildItem -Path .";
        var hover = session.Hover(code, 3); // inside "Get-ChildItem"
        Assert.IsNotNull(hover, "expected hover for a cmdlet");
        StringAssert.Contains(hover.Markdown, "Get-ChildItem");
        StringAssert.Contains(hover.Markdown, "Cmdlet");
    }

    [TestMethod]
    public void Hover_on_session_variable_reports_type_and_value() {
        using var session = new PowerShellSession();
        session.Execute("$answer = 42");
        var hover = session.Hover("$answer", 3);
        Assert.IsNotNull(hover, "expected hover for a session variable");
        StringAssert.Contains(hover.Markdown, "$answer");
        StringAssert.Contains(hover.Markdown, "Int32");
        StringAssert.Contains(hover.Markdown, "42");
    }

    [TestMethod]
    public void SignatureHelp_lists_parameter_sets() {
        using var session = new PowerShellSession();
        const string code = "Get-ChildItem ";
        var help = session.SignatureHelp(code, code.Length); // cursor after the space
        Assert.IsNotNull(help, "expected signature help inside a command call");
        Assert.IsTrue(help.Signatures.Count > 0);
        Assert.IsTrue(help.Signatures.Any(s => s.Label.Contains("Path")),
            "a parameter set should mention -Path");
    }

    [TestMethod]
    public void Markdown_pwsh_fence_becomes_pwsh_block() {
        var md = "# Title\n\n```powershell\nGet-Date\n```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!pwsh\n");
        StringAssert.Contains(blocks[0], "Get-Date");
    }

    [TestMethod]
    public void Dib_pwsh_section_becomes_pwsh_block() {
        var dib = "#!csharp\nvar x = 1;\n#!pwsh\nGet-Location\n";
        var blocks = NotebookImporter.ParseDib(dib);
        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("var x = 1;", blocks[0]);
        StringAssert.StartsWith(blocks[1], "#!pwsh\n");
        StringAssert.Contains(blocks[1], "Get-Location");
    }
}
