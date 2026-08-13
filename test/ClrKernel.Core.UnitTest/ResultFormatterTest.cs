using ClrKernel.Formatting.Html;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class ResultFormatterTest {
    private static (string html, string plain) Format(object value) {
        var dd = ResultFormatter.Format(value);
        return ((string)dd.Data["text/html"], (string)dd.Data["text/plain"]);
    }

    [TestMethod]
    public void Scalar_renders_value_and_type_badge() {
        var (html, plain) = Format(42);
        Assert.AreEqual("42", plain);
        StringAssert.Contains(html, ">42<");
        StringAssert.Contains(html, "ⓘ int");
        StringAssert.Contains(html, "<details");   // click-to-expand
        StringAssert.Contains(html, "title=\"int\""); // hover tooltip
    }

    [TestMethod]
    public void Anonymous_type_renders_clean_not_mangled() {
        var (html, plain) = Format(new { x = 10 });
        Assert.AreEqual("{ x = 10 }", plain);
        StringAssert.Contains(html, "{ x = 10 }");
        Assert.IsFalse(html.Contains("f__AnonymousType"), "mangled compiler name leaked into output");
        StringAssert.Contains(html, "ⓘ anonymous");
        StringAssert.Contains(html, "anonymous { x: int }"); // readable shape in the tooltip/detail
    }

    [TestMethod]
    public void Scalar_sequence_renders_a_value_table() {
        var (html, plain) = Format(new[] { 1, 2, 3 });
        StringAssert.Contains(html, "<table>");
        StringAssert.Contains(html, "<th>Value</th>");
        StringAssert.Contains(html, "<td>1</td>");
        StringAssert.Contains(html, "<td>3</td>");
        StringAssert.Contains(html, "ⓘ int[] — 3 items");
        Assert.AreEqual("[1, 2, 3]", plain);
    }

    [TestMethod]
    public void Object_sequence_renders_property_columns() {
        var (html, _) = Format(new[] { new { Name = "a", Age = 1 }, new { Name = "b", Age = 2 } });
        StringAssert.Contains(html, "<th>Name</th>");
        StringAssert.Contains(html, "<th>Age</th>");
        StringAssert.Contains(html, "<td>a</td>");
        StringAssert.Contains(html, "<td>2</td>");
    }

    [TestMethod]
    public void Object_without_meaningful_tostring_renders_a_property_table() {
        var (html, _) = Format(new Sample { Id = 7, Label = "seven" });
        StringAssert.Contains(html, "<th>Property</th>");
        StringAssert.Contains(html, "<td>Id</td>");
        StringAssert.Contains(html, "<td>7</td>");
        StringAssert.Contains(html, "<td>Label</td>");
        StringAssert.Contains(html, "<td>seven</td>");
    }

    [TestMethod]
    public void Html_in_values_is_encoded() {
        var (html, _) = Format(new[] { "<script>" });
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<script>"), "raw HTML from a value was not encoded");
    }

    private sealed class Sample {
        public int Id { get; set; }
        public string Label { get; set; }
    }
}
