using System;
using System.IO;
using ClrKernel.Runner;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class RunnerParametersTest {
    [TestMethod]
    public void Inferred_scalars_render_as_typed_literals() {
        var p = new RunnerParameters();
        p.SetInferred("b", "true");
        p.SetInferred("i", "5");
        p.SetInferred("d", "0.6");
        p.SetInferred("whole", "3");
        p.SetInferred("big", "9999999999");   // > int.MaxValue -> long
        p.SetInferred("s", "hello");

        Assert.AreEqual("true", p.LiteralFor("b"));
        Assert.AreEqual("5", p.LiteralFor("i"));
        Assert.AreEqual("0.6", p.LiteralFor("d"));
        Assert.AreEqual("3", p.LiteralFor("whole"));
        Assert.AreEqual("9999999999L", p.LiteralFor("big"));
        Assert.AreEqual("\"hello\"", p.LiteralFor("s"));
    }

    [TestMethod]
    public void Raw_values_are_always_strings() {
        var p = new RunnerParameters();
        p.SetRaw("n", "5");       // stays a string despite looking numeric
        p.SetRaw("q", "a\"b\\c"); // quotes and backslashes escaped

        Assert.AreEqual("\"5\"", p.LiteralFor("n"));
        Assert.AreEqual("\"a\\\"b\\\\c\"", p.LiteralFor("q"));
    }

    [TestMethod]
    public void MergeYaml_infers_scalar_types() {
        var p = new RunnerParameters();
        p.MergeYaml("a: 1\nb: two\nc: true\nd: 2.5");

        Assert.AreEqual("1", p.LiteralFor("a"));
        Assert.AreEqual("\"two\"", p.LiteralFor("b"));
        Assert.AreEqual("true", p.LiteralFor("c"));
        Assert.AreEqual("2.5", p.LiteralFor("d"));
    }

    [TestMethod]
    public void MergeYaml_renders_sequences_and_maps() {
        var p = new RunnerParameters();
        p.MergeYaml("nums: [1, 2, 3]\nnested: {x: 1, y: hi}");

        Assert.AreEqual("new object[] { 1, 2, 3 }", p.LiteralFor("nums"));
        Assert.AreEqual(
            "new System.Collections.Generic.Dictionary<string, object> { [\"x\"] = 1, [\"y\"] = \"hi\" }",
            p.LiteralFor("nested"));
    }

    [TestMethod]
    public void Last_value_for_a_name_wins() {
        var p = new RunnerParameters();
        p.SetInferred("a", "1");
        p.SetRaw("a", "override");

        Assert.AreEqual("\"override\"", p.LiteralFor("a"));
        Assert.AreEqual(1, p.Count); // same name, not two entries
    }

    [TestMethod]
    public void RenderCell_emits_var_declarations_or_null_when_empty() {
        Assert.IsNull(new RunnerParameters().RenderCell());

        var p = new RunnerParameters();
        p.SetInferred("count", "5");
        var cell = p.RenderCell();

        StringAssert.Contains(cell, "// clrkernel:injected-parameters");
        StringAssert.Contains(cell, "var count = 5;");
    }

    [TestMethod]
    public void Invalid_parameter_name_is_rejected() {
        Assert.ThrowsExactly<ArgumentException>(() => new RunnerParameters().SetInferred("1bad", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => new RunnerParameters().SetInferred("has space", "x"));
    }
}

[TestClass]
public class RunnerOptionsTest {
    [TestMethod]
    public void Parses_input_path_and_parameters() {
        var o = RunnerOptions.Parse(new[] { "notebook.nb.md", "-p", "a", "1", "-r", "b", "two" });

        Assert.AreEqual("notebook.nb.md", o.InputPath);
        Assert.AreEqual("1", o.Parameters.LiteralFor("a"));
        Assert.AreEqual("\"two\"", o.Parameters.LiteralFor("b"));
    }

    [TestMethod]
    public void CommandLine_p_overrides_yaml_base_regardless_of_order() {
        // -y is the base layer even though it appears after -p on the line.
        var o = RunnerOptions.Parse(new[] { "nb.nb.md", "-p", "a", "9", "-y", "{a: 1, b: 2}" });

        Assert.AreEqual("9", o.Parameters.LiteralFor("a")); // -p wins over -y
        Assert.AreEqual("2", o.Parameters.LiteralFor("b")); // -y-only key preserved
    }

    [TestMethod]
    public void Help_flag_sets_help_requested() {
        Assert.IsTrue(RunnerOptions.Parse(new[] { "--help" }).HelpRequested);
        Assert.IsTrue(RunnerOptions.Parse(new[] { "-h" }).HelpRequested);
    }

    [TestMethod]
    public void Missing_notebook_throws() {
        Assert.ThrowsExactly<ArgumentException>(() => RunnerOptions.Parse(new[] { "-p", "a", "1" }));
    }

    [TestMethod]
    public void Missing_parameters_file_throws() {
        Assert.ThrowsExactly<FileNotFoundException>(
            () => RunnerOptions.Parse(new[] { "nb.nb.md", "-f", "/no/such/file.yaml" }));
    }

    [TestMethod]
    public void Unknown_option_throws() {
        Assert.ThrowsExactly<ArgumentException>(() => RunnerOptions.Parse(new[] { "nb.nb.md", "--bogus" }));
    }
}
