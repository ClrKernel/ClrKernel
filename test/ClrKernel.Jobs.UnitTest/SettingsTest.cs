using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The settings registry: web-writable values round-trip through settings.json,
/// everything dangerous is refused, and secrets never leave the server.
/// </summary>
[TestClass]
public class SettingsTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private JobsOptions Options(Dictionary<string, string> flags = null) {
        flags ??= new Dictionary<string, string>();
        flags["data-dir"] = _dir;
        return JobsOptions.Resolve(flags);
    }

    private static Dictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    [TestMethod]
    public void A_web_writable_value_persists_and_resolves_with_settings_provenance() {
        var registry = SettingsRegistry.CreateDefault(Options());

        Assert.IsNull(registry.Write("general", Values("{\"maxParallelism\": 8}")));

        var reloaded = Options();
        Assert.AreEqual(8, reloaded.MaxParallelism);
        Assert.AreEqual("settings.json", reloaded.SourceOf("maxParallelism"));
    }

    [TestMethod]
    public void Writes_merge_into_existing_settings_rather_than_replacing_the_file() {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"store\":\"files\",\"apiKey\":\"k\"}");
        var registry = SettingsRegistry.CreateDefault(Options());

        Assert.IsNull(registry.Write("general", Values("{\"maxParallelism\": 2}")));

        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json"))).RootElement;
        Assert.AreEqual("files", json.GetProperty("store").GetString(), "unrelated keys survive");
        Assert.AreEqual("k", json.GetProperty("apiKey").GetString());
        Assert.AreEqual(2, json.GetProperty("maxParallelism").GetInt32());
    }

    [TestMethod]
    public void Host_only_fields_are_refused_even_when_named_correctly() {
        var registry = SettingsRegistry.CreateDefault(Options());

        var error = registry.Write("security", Values("{\"apiKey\": \"sneaky\"}"));
        StringAssert.Contains(error, "cannot be changed from the web UI");

        error = registry.Write("general", Values("{\"store\": \"postgres\"}"));
        StringAssert.Contains(error, "cannot be changed from the web UI");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "settings.json")), "nothing was written");
    }

    [TestMethod]
    public void A_value_pinned_by_cli_or_env_is_refused_with_its_source() {
        var registry = SettingsRegistry.CreateDefault(
            Options(new Dictionary<string, string> { ["max-parallelism"] = "6" }));

        var error = registry.Write("general", Values("{\"maxParallelism\": 2}"));
        StringAssert.Contains(error, "--max-parallelism");
    }

    [TestMethod]
    public void Type_validation_refuses_garbage() {
        var registry = SettingsRegistry.CreateDefault(Options());

        StringAssert.Contains(
            registry.Write("general", Values("{\"maxParallelism\": \"lots\"}")), "whole number");
        StringAssert.Contains(
            registry.Write("general", Values("{\"maxParallelism\": 0}")), "whole number");
        StringAssert.Contains(registry.Write("general", Values("{\"nope\": 1}")), "no setting named");
        StringAssert.Contains(registry.Write("nope", Values("{\"x\": 1}")), "No settings section");
    }

    [TestMethod]
    public void Secrets_report_presence_but_never_the_value() {
        var options = Options(new Dictionary<string, string> {
            ["api-key"] = "super-secret-key",
            ["connection-string"] = "Host=db;Password=hunter2",
        });
        var registry = SettingsRegistry.CreateDefault(options);

        var security = registry.Find("security");
        var apiKey = security.Fields.Single(f => f.Name == "apiKey");
        Assert.AreEqual("secret", apiKey.Type);
        Assert.IsTrue(apiKey.IsSet == true);
        Assert.IsNull(apiKey.Value);

        // Belt and braces: the serialized payload the API would return must not
        // contain either secret anywhere.
        var serialized = JsonSerializer.Serialize(registry.Sections);
        Assert.IsFalse(serialized.Contains("super-secret-key"), serialized);
        Assert.IsFalse(serialized.Contains("hunter2"), serialized);
    }
}
