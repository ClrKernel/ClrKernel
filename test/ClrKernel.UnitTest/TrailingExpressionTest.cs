using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A cell that is a single value expression should display its value whether or
/// not it ends with a semicolon. Bare <c>x</c> already returns its value; adding
/// the semicolon a linter asks for (<c>x;</c>) used to be a C# error (CS0201,
/// "only assignment, call, increment, decrement, await, and new object
/// expressions can be used as a statement"). The engine now drops the trailing
/// semicolon for such cells so they print instead of erroring.
/// </summary>
[TestClass]
public class TrailingExpressionTest {
    private static InteractiveScriptEngine NewEngine() =>
        new(Directory.GetCurrentDirectory(), NullLogger.Instance);

    private static string Text(object result) =>
        result is DisplayData d && d.Data.TryGetValue("text/plain", out var t) ? t?.ToString()?.Trim() : null;

    [TestMethod]
    public async Task Bare_expression_without_semicolon_still_prints() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var x = 10;");
        Assert.AreEqual("10", Text(await engine.ExecuteAsync("x")));
    }

    [TestMethod]
    public async Task Expression_with_semicolon_now_prints() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var x = 10;");
        Assert.AreEqual("10", Text(await engine.ExecuteAsync("x;")));
    }

    [TestMethod]
    public async Task Arithmetic_expression_with_semicolon_prints() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var x = 10;");
        Assert.AreEqual("15", Text(await engine.ExecuteAsync("x + 5;")));
    }

    [TestMethod]
    public async Task Member_access_expression_with_semicolon_prints() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var s = \"hello\";");
        Assert.AreEqual("5", Text(await engine.ExecuteAsync("s.Length;")));
    }

    [TestMethod]
    public async Task Declaration_is_unchanged() {
        var engine = NewEngine();
        Assert.IsNull(await engine.ExecuteAsync("var y = 2;"));
    }

    [TestMethod]
    public async Task Method_call_statement_is_unchanged() {
        var engine = NewEngine();
        // An invocation is a legal statement; it must not be turned into a value
        // expression (no spurious display of a void/return value).
        var result = await engine.ExecuteAsync("System.Console.WriteLine(\"hi\");");
        Assert.IsNull(result, "a call statement should not display a value");
    }

    [TestMethod]
    public async Task Assignment_statement_is_unchanged() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var x = 10;");
        // Assignment is a legal statement; keep existing semantics (no display).
        Assert.IsNull(await engine.ExecuteAsync("x = 20;"));
        Assert.AreEqual("20", Text(await engine.ExecuteAsync("x")));
    }
}
