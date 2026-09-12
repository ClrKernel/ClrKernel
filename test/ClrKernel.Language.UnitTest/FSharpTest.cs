using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.FSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// One F# Interactive session per notebook: a trailing expression is the cell's
/// value, bindings persist, printed output goes to the console the kernel is
/// capturing, and a cell that does not compile says so in fsi's words.
/// </summary>
[TestClass]
public class FSharpTest {
    [TestMethod]
    public void A_trailing_expression_is_the_value_and_bindings_persist() {
        using var session = new FSharpSession();
        Assert.IsNull(session.Execute("let x = 40"), "a binding shows nothing");
        Assert.AreEqual(42, session.Execute("x + 2"));
        Assert.AreEqual("hi there", session.Execute("let greet who = sprintf \"hi %s\" who\ngreet \"there\""));
    }

    [TestMethod]
    public void Printed_output_reaches_whatever_console_is_current() {
        using var session = new FSharpSession();
        var before = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try {
            Assert.IsNull(session.Execute("printfn \"from fsharp %d\" 7"), "printfn returns unit, which shows nothing");
        } finally {
            Console.SetOut(before);
        }
        StringAssert.Contains(captured.ToString(), "from fsharp 7");
    }

    [TestMethod]
    public void A_cell_that_does_not_compile_throws_with_the_diagnostic() {
        using var session = new FSharpSession();
        var e = Assert.ThrowsExactly<FSharpCellException>(() => session.Execute("let y: int = \"no\""));
        StringAssert.Contains(e.Message, "FS0001");
        // Annotated: a bare `failwith` has a generic type, and fsi refuses that
        // (FS0030, the value restriction) before it ever runs.
        var raised = Assert.ThrowsExactly<FSharpCellException>(() => session.Execute("(failwith \"boom\" : int)"));
        StringAssert.Contains(raised.Message, "boom");
        Assert.AreEqual(3, session.Execute("1 + 2"), "the session survives a failed cell");
    }

    [TestMethod]
    public async Task Engine_routes_fsharp_cells_to_the_session() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        await engine.ExecuteAsync("#!fsharp\nlet answer = 6 * 7");
        var result = await engine.ExecuteAsync("#!fs\nanswer");
        Assert.AreEqual("42", ((string)((DisplayData)result).Data["text/plain"]).Trim(),
            "a trailing F# value is bundled like a C# one, so every front renders it");
    }
}
