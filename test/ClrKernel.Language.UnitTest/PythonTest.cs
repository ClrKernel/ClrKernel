using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Language.Python;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// `#!python` cells: one resident interpreter per notebook, so a name bound in one
/// cell is there in the next, and output arrives while the cell is still running.
/// </summary>
[TestClass]
public class PythonTest {
    /// <summary>
    /// Skips unless this machine has an interpreter that actually runs.
    ///
    /// <para>
    /// The exit code, not merely the presence of a binary — the lesson from
    /// `bash.exe` on Windows, which exists as WSL's launcher and cannot run
    /// anything when no distribution is installed.
    /// </para>
    /// </summary>
    private static PythonSession RequirePython() {
        // Never downloads: these tests are about running cells against an
        // interpreter this machine has, and a unit test that fetches 60 MB the first
        // time a CI image lacks Python is a test that fails for the wrong reason.
        Environment.SetEnvironmentVariable(PythonProvisioner.AutoInstallVariable, "0");
        var session = new PythonSession();
        try {
            var result = session.ExecuteAsync("pass", null).GetAwaiter().GetResult();
            if (result.Failed) {
                session.Dispose();
                Assert.Inconclusive($"Python is present but cannot run a cell: {result.Error}");
            }
            return session;
        } catch (PythonCellException e) {
            session.Dispose();
            Assert.Inconclusive(e.Message);
            throw;
        }
    }

    [TestMethod]
    public async Task A_cell_runs_and_its_output_comes_back() {
        using var session = RequirePython();
        var result = await session.ExecuteAsync("print('hello from python')", null);

        Assert.IsFalse(result.Failed, result.Error);
        Assert.AreEqual("hello from python\n", result.Output);
    }

    [TestMethod]
    public async Task State_persists_between_cells() {
        using var session = RequirePython();
        await session.ExecuteAsync("x = 41", null);
        var result = await session.ExecuteAsync("print(x + 1)", null);

        Assert.AreEqual("42\n", result.Output, "the second cell shares the first cell's namespace");
    }

    [TestMethod]
    public async Task Output_arrives_while_the_cell_is_still_running() {
        using var session = RequirePython();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(DateTime At, string Text)>();
        session.OnOutput = chunk => seen.Enqueue((DateTime.UtcNow, chunk));

        var started = DateTime.UtcNow;
        var result = await session.ExecuteAsync(
            "import time\nfor i in range(3):\n    print(i)\n    time.sleep(0.4)", null);

        // Not a chunk count: `print(i)` is two writes — the value, then the newline —
        // so the framing that keeps order also splits a line in two. What matters is
        // when they arrive, not how they are cut up.
        Assert.AreEqual("0\n1\n2\n", result.Output);
        var first = seen.First().At;
        var last = seen.Last().At;
        Assert.IsTrue((first - started).TotalSeconds < 0.8,
            $"the first chunk took {(first - started).TotalSeconds:0.00}s; it did not stream");
        // And they were spread across the run rather than delivered together at the
        // end, which is what a buffered pipe looks like.
        Assert.IsTrue((last - first).TotalSeconds > 0.5,
            $"every chunk arrived within {(last - first).TotalSeconds:0.00}s — that is a lump, not a stream");
    }

    [TestMethod]
    public async Task An_error_is_reported_and_the_session_survives_it() {
        using var session = RequirePython();
        var failed = await session.ExecuteAsync("raise ValueError('boom')", null);

        Assert.IsTrue(failed.Failed);
        StringAssert.Contains(failed.Error, "ValueError: boom");
        StringAssert.Contains(failed.Error, "<cell>", "the traceback points at the cell");
        // And says nothing about the plumbing: the frame that ran `exec` is this
        // kernel's driver, and a user reading their own traceback should not meet it.
        Assert.IsFalse(failed.Error.Contains("driver"),
            $"the driver's own frame leaked into the traceback:\n{failed.Error}");
        Assert.IsFalse(failed.Error.Contains("exec(compile"), failed.Error);

        var after = await session.ExecuteAsync("print('still here')", null);
        Assert.AreEqual("still here\n", after.Output, "the interpreter outlives a failed cell");
    }

    /// <summary>Output written before the error is worth keeping: a traceback usually
    /// only makes sense with whatever the cell printed on the way to it.</summary>
    [TestMethod]
    public async Task A_failing_cell_keeps_the_output_it_produced_first() {
        using var session = RequirePython();
        var result = await session.ExecuteAsync("print('before')\nraise RuntimeError('after')", null);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.Output, "before");
    }

    [TestMethod]
    public async Task A_restart_clears_every_name() {
        using var session = RequirePython();
        await session.ExecuteAsync("kept = 'yes'", null);
        session.Restart();
        var result = await session.ExecuteAsync("print('kept' in dir())", null);

        Assert.AreEqual("False\n", result.Output);
    }

    /// <summary>
    /// stdout and stderr arrive in the order the cell wrote them. They are one
    /// channel for exactly this reason: as two pipes, a cell's last line could
    /// arrive after the message saying the cell had finished.
    /// </summary>
    [TestMethod]
    public async Task Output_keeps_the_order_the_cell_wrote_it_in() {
        using var session = RequirePython();
        var result = await session.ExecuteAsync(
            "import sys\n"
            + "print('one')\n"
            + "sys.stderr.write('two\\n')\n"
            + "print('three')", null);

        Assert.AreEqual("one\ntwo\nthree\n", result.Output);
    }

    [TestMethod]
    public void An_interpreter_that_is_not_there_says_where_it_looked() {
        var message = PythonInterpreter.NotFoundMessage();

        StringAssert.Contains(message, PythonInterpreter.PathVariable);
        StringAssert.Contains(message, "PATH");
    }

    /// <summary>
    /// The uv build this machine would download. Not a network test — it is the
    /// mapping that decides whether a container gets a binary it can run, and musl
    /// is the case that bites: an Alpine image cannot execute the gnu build.
    /// </summary>
    [TestMethod]
    public void The_uv_asset_matches_this_platform() {
        var asset = PythonProvisioner.AssetName();

        StringAssert.Contains(asset, "uv-");
        if (OperatingSystem.IsWindows()) {
            StringAssert.Contains(asset, "windows");
            StringAssert.EndsWith(asset, ".zip");
        } else if (OperatingSystem.IsMacOS()) {
            StringAssert.Contains(asset, "apple-darwin");
            StringAssert.EndsWith(asset, ".tar.gz");
        } else {
            StringAssert.Contains(asset, "linux");
        }
        var arm = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64;
        StringAssert.Contains(asset, arm ? "aarch64" : "x86_64");
    }

    /// <summary>Turning it off has to mean off — the air-gapped promise.</summary>
    [TestMethod]
    public async Task With_installation_off_and_no_interpreter_it_refuses_rather_than_fetching() {
        var savedPath = Environment.GetEnvironmentVariable(PythonInterpreter.PathVariable);
        var savedHome = Environment.GetEnvironmentVariable(PythonInterpreter.HomeVariable);
        var savedAuto = Environment.GetEnvironmentVariable(PythonProvisioner.AutoInstallVariable);
        var empty = Path.Combine(Path.GetTempPath(), "clrkernel-nopy-" + Guid.NewGuid().ToString("N"));
        try {
            // An explicit interpreter that does not exist, so nothing on this machine
            // can satisfy the lookup, and installation off.
            Environment.SetEnvironmentVariable(PythonInterpreter.PathVariable, Path.Combine(empty, "python3"));
            Environment.SetEnvironmentVariable(PythonInterpreter.HomeVariable, empty);
            Environment.SetEnvironmentVariable(PythonProvisioner.AutoInstallVariable, "0");

            using var session = new PythonSession();
            var e = await Assert.ThrowsExactlyAsync<PythonCellException>(
                () => session.ExecuteAsync("print(1)", null));
            StringAssert.Contains(e.Message, "Could not start Python");
        } finally {
            Environment.SetEnvironmentVariable(PythonInterpreter.PathVariable, savedPath);
            Environment.SetEnvironmentVariable(PythonInterpreter.HomeVariable, savedHome);
            Environment.SetEnvironmentVariable(PythonProvisioner.AutoInstallVariable, savedAuto);
        }
    }
}
