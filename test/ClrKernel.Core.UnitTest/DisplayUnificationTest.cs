using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Formatting.Html;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The two display paths — a trailing cell value and an explicit Display() — must be
/// one path: both go through the DisplayFormatters registry, and a display handle is
/// never itself rendered.
/// </summary>
[TestClass]
public class DisplayUnificationTest {
    private static InteractiveScriptEngine NewEngine() =>
        new(Directory.GetCurrentDirectory(), NullLogger.Instance);

    [TestMethod]
    public async Task A_trailing_Display_call_shows_the_value_once_not_the_handle() {
        var displays = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            var engine = NewEngine();
            var result = await engine.ExecuteAsync("\"hi\".Display()");

            Assert.IsNull(result, "the DisplayCell handle must be suppressed as a trailing value");
            Assert.AreEqual(1, displays.Count, "the value itself must have been displayed exactly once");
            Assert.AreEqual("hi", displays[0].Data["text/plain"]);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
        }
    }

    [TestMethod]
    public async Task A_trailing_DisplayHtml_handle_is_also_suppressed() {
        var displays = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            var engine = NewEngine();
            var result = await engine.ExecuteAsync("\"<b>x</b>\".DisplayHtml()");

            Assert.IsNull(result, "a DisplayCell handle must be suppressed whatever created it");
            Assert.AreEqual(1, displays.Count);
            Assert.AreEqual("<b>x</b>", displays[0].Data["text/html"]);
            Assert.IsFalse(displays[0].Data.ContainsKey("text/plain"),
                "raw HTML must not leak into text/plain");
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
        }
    }

    [TestMethod]
    public async Task A_trailing_value_and_Display_render_identically_with_the_plugin() {
        HtmlFormatters.RegisterDefaults();
        var displays = new List<DisplayData>();
        try {
            DisplayDataEmitter.DisplayDataHandler = displays.Add;
            var engine = NewEngine();

            var trailing = (DisplayData)await engine.ExecuteAsync("new { Name = \"x\", Count = 3 }");
            await engine.ExecuteAsync("new { Name = \"x\", Count = 3 }.Display();");

            Assert.AreEqual(1, displays.Count);
            Assert.AreEqual(trailing.Data["text/html"], displays[0].Data["text/html"],
                "Display(x) and a bare trailing x must produce the same render");
            StringAssert.Contains((string)trailing.Data["text/html"], "clrkernel-result");
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            HtmlFormatters.UnregisterDefaults();
        }
    }

    [TestMethod]
    public async Task A_trailing_concept_value_goes_through_the_registry() {
        HtmlFormatters.RegisterDefaults();
        try {
            var engine = NewEngine();
            var result = (DisplayData)await engine.ExecuteAsync(
                "new ClrKernel.Core.Primitives.DisplayConsoleText(\"\\u001b[31mred\\u001b[0m\")");
            Assert.AreEqual("red", result.Data["text/plain"], "ANSI escapes must be stripped from text");
            StringAssert.Contains((string)result.Data["text/html"], "ansi");
        } finally {
            HtmlFormatters.UnregisterDefaults();
        }
    }
}
