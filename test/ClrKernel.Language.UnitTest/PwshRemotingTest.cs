using System;
using ClrKernel.Language.PowerShell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class PwshRemotingTest {
    [TestMethod]
    public void ParseConnect_defaults_to_ssh() {
        var spec = PwshDirectives.ParseConnect("#!pwsh-connect --name srv --host srv01 --user admin --identity /k");
        Assert.AreEqual(PwshTransport.Ssh, spec.Transport);
        Assert.AreEqual("srv01", spec.Host);
        Assert.AreEqual("/k", spec.IdentityFile);
    }

    [TestMethod]
    public void ParseConnect_winrm_takes_a_secret_reference() {
        var spec = PwshDirectives.ParseConnect(
            "#!pwsh-connect --name srv --host srv01 --winrm --user CONTOSO\\svc --secret ps:srv01 --use-ssl --port 5987");
        Assert.AreEqual(PwshTransport.WinRm, spec.Transport);
        Assert.AreEqual("CONTOSO\\svc", spec.User);
        Assert.AreEqual("ps:srv01", spec.SecretRef);
        Assert.IsTrue(spec.UseSsl);
        Assert.AreEqual(5987, spec.Port);
    }

    [TestMethod]
    public void ParseConnect_rejects_an_inline_password() {
        var e = Assert.ThrowsExactly<FormatException>(() =>
            PwshDirectives.ParseConnect("#!pwsh-connect --name x --host h --password hunter2"));
        StringAssert.Contains(e.Message, "--secret");
    }

    [TestMethod]
    public void Winrm_with_a_user_but_no_secret_fails_with_direction() {
        var spec = new PwshConnectionSpec { Name = "srv", Host = "h", User = "u", Transport = PwshTransport.WinRm };
        var e = Assert.ThrowsExactly<PowerShellCellException>(
            () => spec.CreateConnectionInfo(new ClrKernel.Core.Secrets.SecretStore()));
        StringAssert.Contains(e.Message, "--secret");
    }

    [TestMethod]
    public void Winrm_without_a_user_uses_the_current_identity() {
        var spec = new PwshConnectionSpec { Name = "srv", Host = "h", Transport = PwshTransport.WinRm };
        var info = spec.CreateConnectionInfo(new ClrKernel.Core.Secrets.SecretStore());
        Assert.IsInstanceOfType(info, typeof(System.Management.Automation.Runspaces.WSManConnectionInfo));
    }

    [TestMethod]
    public void Ssh_transport_builds_an_ssh_connection_info() {
        var spec = new PwshConnectionSpec { Name = "srv", Host = "h", User = "u", IdentityFile = "/k", Port = 2222 };
        var info = spec.CreateConnectionInfo(new ClrKernel.Core.Secrets.SecretStore());
        Assert.IsInstanceOfType(info, typeof(System.Management.Automation.Runspaces.SSHConnectionInfo));
    }

    [TestMethod]
    public void A_shared_Ssh_config_node_is_usable_by_powershell() {
        // One "$type": "Ssh" host definition serves both shell and pwsh cells.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck-pwsh-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try {
            var file = System.IO.Path.Combine(dir, "connections.json");
            System.IO.File.WriteAllText(file, """
                { "web01": { "$type": "Ssh", "host": "web01.example.com", "user": "deploy" } }
                """);
            var node = System.Linq.Enumerable.Single(ClrKernel.Database.ConnectionConfig.LoadAllRaw(file));
            var spec = PwshConnectionConfig.FromNode(node);
            Assert.AreEqual(PwshTransport.Ssh, spec.Transport);
            Assert.AreEqual("web01.example.com", spec.Host);

            using var session = new PowerShellSession();
            CollectionAssert.Contains((System.Collections.ICollection)session.LoadFromConfig(dir), "web01");
        } finally {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void SelectorConnection_reads_the_flag_or_null() {
        Assert.AreEqual("srv", PwshDirectives.SelectorConnection("#!pwsh --connection srv"));
        Assert.IsNull(PwshDirectives.SelectorConnection("#!pwsh"));
    }

    // Live end-to-end against a Windows (or any) box, opt-in:
    // CLRKERNEL_TEST_PSREMOTE="[user@]host[:port]" — PowerShell-over-SSH, so the
    // remote needs PowerShell and the sshd "Subsystem powershell" entry.
    [TestMethod]
    public async System.Threading.Tasks.Task Live_psremoting_over_ssh_when_configured() {
        var target = Environment.GetEnvironmentVariable("CLRKERNEL_TEST_PSREMOTE");
        if (string.IsNullOrEmpty(target)) {
            Assert.Inconclusive("Set CLRKERNEL_TEST_PSREMOTE=[user@]host[:port] to run the live PSRemoting test.");
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
        var engine = new ClrKernel.Core.Scripting.InteractiveScriptEngine(
            System.IO.Directory.GetCurrentDirectory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await engine.ExecuteAsync(
            $"#!pwsh-connect --name livewin --host {host}{(user != null ? $" --user {user}" : "")}{port}");

        string Text(object r) => ((ClrKernel.Core.Scripting.DisplayData)r).Data["text/plain"].ToString();

        var version = Text(await engine.ExecuteAsync("#!pwsh --connection livewin\n$PSVersionTable.PSVersion.Major"));
        Assert.IsTrue(int.TryParse(version.Trim(), out var major) && major >= 5, $"unexpected version output: {version}");

        // Remote runspace state persists across cells.
        await engine.ExecuteAsync("#!pwsh --connection livewin\n$ck_live = 41");
        var sum = Text(await engine.ExecuteAsync("#!pwsh --connection livewin\n$ck_live + 1"));
        StringAssert.Contains(sum, "42");
    }

    [TestMethod]
    public void An_unknown_connection_names_the_known_ones() {
        using var session = new PowerShellSession();
        session.Connect("#!pwsh-connect --name srv --host h");
        var e = Assert.ThrowsExactly<PowerShellCellException>(() => session.Execute("Get-Date", "missing"));
        StringAssert.Contains(e.Message, "missing");
        StringAssert.Contains(e.Message, "srv");
    }
}
