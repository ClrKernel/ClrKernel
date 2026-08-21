using System;
using System.Linq;
using ClrKernel.Core.Scripting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Core.UnitTest;

/// <summary>
/// The shared directive tokenizer and binder every cell language's <c>#!</c>
/// magics run through. Language-specific semantics live in the language tests;
/// this pins the machinery itself.
/// </summary>
[TestClass]
public class DirectiveParserTest {
    private static readonly DirectiveDefinition _def = new() {
        Selector = "#!demo",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true },
            new() { Name = "--value", Aliases = new[] { "-v" } },
            new() { Name = "--flag", Kind = DirectiveParameterKind.Flag },
            new() { Name = "--other-flag", Kind = DirectiveParameterKind.Flag },
            new() { Name = "--opt", Kind = DirectiveParameterKind.KeyValue, Repeatable = true },
            new() { Name = "--map", Kind = DirectiveParameterKind.KeyValue, KeyValueHint = "source=dest" },
            new() { Name = "--nope", Kind = DirectiveParameterKind.Forbidden, ForbiddenMessage = "Not allowed here." },
            new() { Name = "--list", Required = true, RequiredLabel = "--list <a[,b...]>" },
        },
    };

    // Everything below the required pair, present once so Parse succeeds.
    private static DirectiveArgs Parse(string flags) =>
        DirectiveParser.Parse(_def, $"#!demo --name x --list l {flags}");

    [TestMethod]
    public void Tokenize_handles_quotes_empty_tokens_and_gluing() {
        CollectionAssert.AreEqual(new[] { "a", "b c", "d" }, DirectiveParser.Tokenize("a \"b c\" d").ToArray());
        CollectionAssert.AreEqual(new[] { "b c" }, DirectiveParser.Tokenize("'b c'").ToArray());
        CollectionAssert.AreEqual(new[] { "" }, DirectiveParser.Tokenize("\"\"").ToArray(), "quoted empty is a token");
        CollectionAssert.AreEqual(new[] { "prefix midpost" }, DirectiveParser.Tokenize("pre\"fix mid\"post").ToArray());
        Assert.AreEqual(0, DirectiveParser.Tokenize("   ").Count);
        Assert.AreEqual(0, DirectiveParser.Tokenize(null).Count);
        CollectionAssert.AreEqual(new[] { "a", "b" }, DirectiveParser.Tokenize("  a\t\t b  ").ToArray());
        // An unterminated quote runs to the end of the line rather than throwing.
        CollectionAssert.AreEqual(new[] { "a bc" }, DirectiveParser.Tokenize("a\" bc").ToArray());
    }

    [TestMethod]
    public void Strip_selector_is_case_insensitive_and_leading_only() {
        Assert.AreEqual(" --x", DirectiveParser.StripSelector("#!Demo --x", "#!demo"));
        Assert.AreEqual("body #!demo", DirectiveParser.StripSelector("body #!demo", "#!demo"));
        Assert.AreEqual(string.Empty, DirectiveParser.StripSelector(null, "#!demo"));
    }

    [TestMethod]
    public void Values_bind_last_wins_and_all_are_kept() {
        var args = Parse("--value a -V b");
        Assert.AreEqual("b", args.Get("--value"), "aliases bind to the canonical name, case-insensitively");
        CollectionAssert.AreEqual(new[] { "a", "b" }, args.GetAll("--value").ToArray());
        Assert.IsTrue(args.Has("--value"));
        Assert.IsFalse(args.Has("--flag"));
        Assert.IsNull(args.Get("--flag"));
        Assert.AreEqual(0, args.GetAll("--flag").Count);
    }

    [TestMethod]
    public void Flags_key_values_and_last_of() {
        var args = Parse("--flag --opt a=1 --opt b=2 --opt a=3 --other-flag --flag");
        Assert.IsTrue(args.Has("--flag"));
        var opts = args.KeyValues("--opt");
        Assert.AreEqual("3", opts["a"], "later duplicate keys overwrite");
        Assert.AreEqual("2", opts["b"]);
        Assert.AreEqual(0, args.KeyValues("--map").Count);
        Assert.AreEqual("--flag", args.LastOf("--flag", "--other-flag"));
        Assert.IsNull(args.LastOf("--map"));
    }

    [TestMethod]
    public void Error_messages_match_the_established_shapes() {
        Assert.AreEqual("Unknown #!demo flag 'stray'.",
            Assert.ThrowsExactly<FormatException>(() => Parse("stray")).Message);
        Assert.AreEqual("Missing value for -v.",
            Assert.ThrowsExactly<FormatException>(() => Parse("-v")).Message, "original token spelling, not the canonical name");
        Assert.AreEqual("Not allowed here.",
            Assert.ThrowsExactly<FormatException>(() => Parse("--nope")).Message);
        Assert.AreEqual("--opt expects key=value, got 'broken'.",
            Assert.ThrowsExactly<FormatException>(() => Parse("--opt broken")).Message);
        Assert.AreEqual("--map expects source=dest, got '=x'.",
            Assert.ThrowsExactly<FormatException>(() => Parse("--map =x")).Message);
        Assert.AreEqual("#!demo requires --name.",
            Assert.ThrowsExactly<FormatException>(() =>
                DirectiveParser.Parse(_def, "#!demo --list l")).Message);
        Assert.AreEqual("#!demo requires --name.",
            Assert.ThrowsExactly<FormatException>(() =>
                DirectiveParser.Parse(_def, "#!demo --name \"\" --list l")).Message, "blank counts as missing");
        Assert.AreEqual("#!demo requires --list <a[,b...]>.",
            Assert.ThrowsExactly<FormatException>(() =>
                DirectiveParser.Parse(_def, "#!demo --name x")).Message, "required label overrides the flag name");
    }

    [TestMethod]
    public void Find_value_is_a_tolerant_scan() {
        Assert.AreEqual("box", DirectiveParser.FindValue("#!pwsh --connection box", "--connection"));
        Assert.AreEqual("my box", DirectiveParser.FindValue("#!pwsh --connection \"my box\"", "--connection"),
            "quoted names work — the old regex could not do this");
        Assert.AreEqual("a", DirectiveParser.FindValue("--x 1 --conn a --conn b", "--conn"), "first match wins");
        Assert.IsNull(DirectiveParser.FindValue("#!pwsh --connection", "--connection"), "flag at end of line");
        Assert.IsNull(DirectiveParser.FindValue(null, "--connection"));
    }
}
