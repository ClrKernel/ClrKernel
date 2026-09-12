using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// <c>#!share --from &lt;language&gt; name [--as alias]</c> hands a value across
/// sessions — the same directive a Polyglot notebook uses, so a migrated .dib
/// keeps working. A copy of the reference, bound under the name on the other
/// side with a static type the cell can use.
/// </summary>
[TestClass]
public class ShareTest {
    private static InteractiveScriptEngine NewEngine() =>
        new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);

    private static string Text(object result) => ((string)((DisplayData)result).Data["text/plain"]).Trim();

    [TestMethod]
    public async Task A_csharp_value_is_visible_to_an_fsharp_cell_and_back() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var n = 41;\nvar names = new List<string> { \"ada\", \"bo\" };");

        Assert.AreEqual("42", Text(await engine.ExecuteAsync("#!fsharp\n#!share --from csharp n\nn + 1")));
        Assert.AreEqual("ADA, BO", Text(await engine.ExecuteAsync(
            "#!fsharp\n#!share --from csharp names --as people\nSystem.String.Join(\", \", people |> Seq.map (fun s -> s.ToUpper()))")));

        await engine.ExecuteAsync("#!fsharp\nlet squares = [1 .. 5] |> List.map (fun x -> x * x)\nlet greeting = \"hi\"");
        // The static type is the runtime type, so members resolve in the C# cell.
        Assert.AreEqual("55", Text(await engine.ExecuteAsync("#!share --from fsharp squares\nsquares.Sum()")));
        Assert.AreEqual("HI", Text(await engine.ExecuteAsync("#!share --from fsharp greeting --as msg\nmsg.ToUpper()")));
    }

    [TestMethod]
    public async Task A_missing_variable_or_language_is_a_clear_error() {
        var engine = NewEngine();
        await engine.ExecuteAsync("var here = 1;");
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => engine.ExecuteAsync("#!fsharp\n#!share --from csharp nope\nnope"));
        StringAssert.Contains(missing.Message, "no variable 'nope'");

        var noLanguage = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => engine.ExecuteAsync("#!share --from cobol x\nx"));
        StringAssert.Contains(noLanguage.Message, "no language 'cobol'");

        var cannotReceive = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => engine.ExecuteAsync("#!mermaid\n#!share --from csharp here\ngraph LR; A-->B"));
        StringAssert.Contains(cannotReceive.Message, "cannot receive");
    }

    [TestMethod]
    public void The_directive_parses_its_forms_and_spells_csharp_types() {
        var share = ShareDirective.TryParse("#!share --from csharp total --as sum");
        Assert.AreEqual("csharp", share.From);
        Assert.AreEqual("total", share.Name);
        Assert.AreEqual("sum", share.As);
        Assert.AreEqual("x", ShareDirective.TryParse("#!share --from fsharp x").As, "the alias defaults to the name");
        Assert.IsNull(ShareDirective.TryParse("#!sharex --from a b"), "a different selector is not this one");
        Assert.ThrowsExactly<FormatException>(() => ShareDirective.TryParse("#!share x"));

        var (statement, shares) = ShareDirective.Extract("#!fsharp\n#!share --from csharp a\n#!share --from csharp b\nlet c = a + b\n#!share --from csharp late");
        Assert.AreEqual(2, shares.Count, "only the leading directive block is scanned");
        Assert.AreEqual("#!fsharp\n\n\nlet c = a + b\n#!share --from csharp late", statement, "blanked, so line numbers hold");

        Assert.AreEqual("int", ShareDirective.CSharpTypeName(typeof(int)));
        Assert.AreEqual("global::System.Collections.Generic.List<string>", ShareDirective.CSharpTypeName(typeof(System.Collections.Generic.List<string>)));
        Assert.AreEqual("global::System.Collections.Generic.Dictionary<string, int[]>", ShareDirective.CSharpTypeName(typeof(System.Collections.Generic.Dictionary<string, int[]>)));
        Assert.AreEqual("int?", ShareDirective.CSharpTypeName(typeof(int?)));
        Assert.AreEqual("object", ShareDirective.CSharpTypeName(new { a = 1 }.GetType()), "an anonymous type has no name a cell can write");
    }
}
