using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class ShellRemotingTest {
    [TestMethod]
    public void ParseConnect_reads_the_target() {
        var spec = ShellDirectives.ParseConnect(
            "#!shell-connect --name web01 --host web01.example.com --user deploy --port 2222 --identity \"~/.ssh/id ed25519\"");
        Assert.AreEqual("web01", spec.Name);
        Assert.AreEqual("web01.example.com", spec.Host);
        Assert.AreEqual("deploy", spec.User);
        Assert.AreEqual(2222, spec.Port);
        Assert.AreEqual("~/.ssh/id ed25519", spec.IdentityFile, "quoted paths keep their spaces");
    }

    [TestMethod]
    public void ParseConnect_rejects_an_inline_password() {
        var e = Assert.ThrowsExactly<FormatException>(() =>
            ShellDirectives.ParseConnect("#!shell-connect --name x --host h --password hunter2"));
        StringAssert.Contains(e.Message, "key");
    }

    [TestMethod]
    public void Ssh_arguments_are_batchmode_with_port_identity_and_destination() {
        var spec = new ShellConnectionSpec { Name = "w", Host = "h", User = "u", Port = 2222, IdentityFile = "/k" };
        var args = spec.BuildSshArguments("bash -s");
        CollectionAssert.AreEqual(
            new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-p", "2222", "-i", "/k", "u@h", "bash -s" },
            (System.Collections.ICollection)args);
    }

    [TestMethod]
    public void Default_port_and_missing_user_are_omitted() {
        var args = new ShellConnectionSpec { Name = "w", Host = "h" }.BuildSshArguments("sh -s");
        CollectionAssert.AreEqual(
            new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "h", "sh -s" },
            (System.Collections.ICollection)args);
    }

    [TestMethod]
    public void SelectorConnection_reads_the_flag_or_null() {
        Assert.AreEqual("web01", ShellDirectives.SelectorConnection("#!bash --connection web01"));
        Assert.IsNull(ShellDirectives.SelectorConnection("#!bash"));
    }

    [TestMethod]
    public void The_cwd_marker_is_stripped_and_captured() {
        var (output, cwd) = ShellSession.StripCwdMarker("hello\n\u0001/home/deploy\u0001");
        Assert.AreEqual("hello\n", output);
        Assert.AreEqual("/home/deploy", cwd);
    }

    [TestMethod]
    public void Output_without_a_marker_passes_through() {
        var (output, cwd) = ShellSession.StripCwdMarker("plain output");
        Assert.AreEqual("plain output", output);
        Assert.IsNull(cwd);
    }

    [TestMethod]
    public async Task An_unknown_connection_names_the_known_ones() {
        var session = new ShellSession();
        session.Connect("#!shell-connect --name web01 --host h");
        var e = await Assert.ThrowsExactlyAsync<ShellCellException>(
            () => session.ExecuteRemoteAsync("bash", "echo hi", "missing"));
        StringAssert.Contains(e.Message, "missing");
        StringAssert.Contains(e.Message, "web01");
    }

    [TestMethod]
    public void Ssh_targets_load_from_connections_json() {
        var dir = Path.Combine(Path.GetTempPath(), "ck-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "connections.json"), """
                {
                  "web01": { "$type": "Ssh", "host": "web01.example.com", "user": "deploy", "port": "2222" },
                  "db":    { "$type": "SqlServer", "server": "s", "database": "d", "auth": "integrated" }
                }
                """);
            var loaded = new ShellSession().LoadFromConfig(dir);
            CollectionAssert.AreEqual(new[] { "web01" }, (System.Collections.ICollection)loaded,
                "only Ssh nodes belong to the shell session");
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void Pwsh_connect_inside_a_powershell_fence_passes_through() {
        var blocks = NotebookImporter.ParseMarkdown(
            "```powershell\n#!pwsh-connect --name srv --host x\n```\n\n```powershell\nGet-Date\n```\n");
        Assert.AreEqual(2, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!pwsh-connect", "the selector must not be buried under a prepended #!pwsh");
        StringAssert.StartsWith(blocks[1], "#!pwsh\n");
    }

    // Live end-to-end, opt-in: CLRKERNEL_TEST_SSH="user@host" (key auth must already work).
    [TestMethod]
    public async Task Live_ssh_roundtrip_when_configured() {
        // Accepts user@host, host, and an optional :port on either.
        var target = Environment.GetEnvironmentVariable("CLRKERNEL_TEST_SSH");
        if (string.IsNullOrEmpty(target)) {
            Assert.Inconclusive("Set CLRKERNEL_TEST_SSH=[user@]host[:port] to run the live SSH test.");
        }
        var at = target.IndexOf('@');
        var user = at > 0 ? target.Substring(0, at) : null;
        var host = at >= 0 ? target.Substring(at + 1) : target;
        var port = "";
        var colon = host.IndexOf(':');
        if (colon > 0) {
            port = $" --port {host.Substring(colon + 1)}";
            host = host.Substring(0, colon);
        }
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        await engine.ExecuteAsync(
            $"#!shell-connect --name live --host {host}{(user != null ? $" --user {user}" : "")}{port}");

        var echo = await engine.ExecuteAsync("#!bash --connection live\necho remote-ok");
        Assert.AreEqual("remote-ok", ((DisplayData)echo).Data["text/plain"]);

        // cd carries to the next remote cell.
        await engine.ExecuteAsync("#!bash --connection live\ncd /tmp");
        var pwd = await engine.ExecuteAsync("#!bash --connection live\npwd");
        StringAssert.Contains(((DisplayData)pwd).Data["text/plain"].ToString(), "tmp");
    }
}
