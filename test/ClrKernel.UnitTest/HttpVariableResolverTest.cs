using System;
using System.Collections.Generic;
using ClrKernel.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class HttpVariableResolverTest {
    private static HttpVariableResolver Resolver(
        IReadOnlyDictionary<string, HttpExchange> responses = null, params (string, string)[] vars) {
        var defs = new List<HttpVariableDefinition>();
        foreach (var (name, value) in vars) {
            defs.Add(new HttpVariableDefinition(name, value));
        }
        return new HttpVariableResolver(defs, responses ?? new Dictionary<string, HttpExchange>());
    }

    [TestMethod]
    public void Resolves_file_variables_including_nested() {
        var resolver = Resolver(null, ("host", "https://example.com"), ("url", "{{host}}/api"));
        Assert.AreEqual("https://example.com/api", resolver.Resolve("{{url}}"));
    }

    [TestMethod]
    public void Unknown_variable_is_left_as_literal() {
        var resolver = Resolver();
        Assert.AreEqual("{{missing}}", resolver.Resolve("{{missing}}"));
    }

    [TestMethod]
    public void System_guid_is_a_guid() {
        var resolver = Resolver();
        var value = resolver.Resolve("{{$guid}}");
        Assert.IsTrue(Guid.TryParse(value, out _), "expected a GUID, got: " + value);
    }

    [TestMethod]
    public void System_randomint_respects_range() {
        var resolver = Resolver();
        var value = int.Parse(resolver.Resolve("{{$randomInt 5 10}}"));
        Assert.IsTrue(value >= 5 && value < 10, "out of range: " + value);
    }

    [TestMethod]
    public void System_timestamp_is_numeric() {
        var resolver = Resolver();
        var value = resolver.Resolve("{{$timestamp}}");
        Assert.IsTrue(long.TryParse(value, out var epoch) && epoch > 1_600_000_000, "bad timestamp: " + value);
    }

    [TestMethod]
    public void System_datetime_iso8601_parses() {
        var resolver = Resolver();
        var value = resolver.Resolve("{{$datetime iso8601}}");
        Assert.IsTrue(DateTimeOffset.TryParse(value, out _), "bad iso8601: " + value);
    }

    [TestMethod]
    public void Response_body_json_path_is_resolved() {
        var login = new HttpExchange {
            Name = "login",
            StatusCode = 200,
            BodyText = "{ \"data\": { \"token\": \"secret-123\" }, \"items\": [ { \"id\": 7 } ] }",
        };
        var resolver = Resolver(new Dictionary<string, HttpExchange> { ["login"] = login });

        Assert.AreEqual("secret-123", resolver.Resolve("{{login.response.body.$.data.token}}"));
        Assert.AreEqual("7", resolver.Resolve("{{login.response.body.$.items[0].id}}"));
    }

    [TestMethod]
    public void Response_header_reference_is_resolved() {
        var exchange = new HttpExchange { Name = "create", StatusCode = 201 };
        exchange.ResponseHeaders.Add(new HttpNameValue("Location", "https://example.com/items/42"));
        var resolver = Resolver(new Dictionary<string, HttpExchange> { ["create"] = exchange });

        Assert.AreEqual("https://example.com/items/42",
            resolver.Resolve("{{create.response.headers.Location}}"));
    }
}
