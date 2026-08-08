using System.Linq;
using ClrKernel.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class HttpFileParserTest {
    [TestMethod]
    public void Parses_method_url_headers_and_body() {
        var file = HttpFileParser.Parse(
            "POST https://api.example.com/items HTTP/1.1\n" +
            "Content-Type: application/json\n" +
            "Authorization: Bearer abc\n" +
            "\n" +
            "{ \"name\": \"widget\" }\n");

        Assert.AreEqual(1, file.Requests.Count);
        var request = file.Requests[0];
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("https://api.example.com/items", request.Url);
        Assert.AreEqual("HTTP/1.1", request.Version);
        Assert.AreEqual(2, request.Headers.Count);
        Assert.AreEqual("Content-Type", request.Headers[0].Name);
        Assert.AreEqual("application/json", request.Headers[0].Value);
        StringAssert.Contains(request.Body, "\"name\": \"widget\"");
    }

    [TestMethod]
    public void Bare_url_defaults_to_get() {
        var file = HttpFileParser.Parse("https://example.com/health\n");
        Assert.AreEqual(1, file.Requests.Count);
        Assert.AreEqual("GET", file.Requests[0].Method);
        Assert.AreEqual("https://example.com/health", file.Requests[0].Url);
    }

    [TestMethod]
    public void Splits_multiple_requests_on_separator() {
        var file = HttpFileParser.Parse(
            "GET https://example.com/a\n" +
            "###\n" +
            "GET https://example.com/b\n");
        Assert.AreEqual(2, file.Requests.Count);
        Assert.AreEqual("https://example.com/a", file.Requests[0].Url);
        Assert.AreEqual("https://example.com/b", file.Requests[1].Url);
    }

    [TestMethod]
    public void Collects_variables_and_request_names() {
        var file = HttpFileParser.Parse(
            "@base = https://example.com\n" +
            "@token = xyz\n" +
            "\n" +
            "# @name login\n" +
            "POST {{base}}/login\n");

        Assert.AreEqual(2, file.Variables.Count);
        Assert.AreEqual("base", file.Variables[0].Name);
        Assert.AreEqual("https://example.com", file.Variables[0].Value);
        Assert.AreEqual(1, file.Requests.Count);
        Assert.AreEqual("login", file.Requests[0].Name);
        Assert.AreEqual("{{base}}/login", file.Requests[0].Url);
    }

    [TestMethod]
    public void Comments_are_ignored_but_body_preserved() {
        var file = HttpFileParser.Parse(
            "// a comment\n" +
            "GET https://example.com/x\n" +
            "# another comment\n" +
            "Accept: application/json\n" +
            "\n" +
            "line one\n" +
            "line two\n");
        var request = file.Requests.Single();
        Assert.AreEqual(1, request.Headers.Count);
        Assert.AreEqual("line one\nline two", request.Body);
    }

    [TestMethod]
    public void Body_from_file_directive_is_detected() {
        var file = HttpFileParser.Parse(
            "POST https://example.com/upload\n" +
            "\n" +
            "<@ ./payload.json\n");
        var request = file.Requests.Single();
        Assert.AreEqual("./payload.json", request.BodyFromFile);
        Assert.IsTrue(request.BodyFileInterpolate);
        Assert.IsNull(request.Body);
    }
}
