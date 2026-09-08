using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
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

    /// <summary>
    /// A network that blocks github.com but proxies it can point the fetch elsewhere.
    /// The interpreter half needs no equivalent — uv reads UV_PYTHON_INSTALL_MIRROR
    /// and inherits it — so this one variable is the whole gap.
    /// </summary>
    [TestMethod]
    public void A_mirror_replaces_the_uv_download_host() {
        var saved = Environment.GetEnvironmentVariable(PythonProvisioner.UvMirrorVariable);
        try {
            Environment.SetEnvironmentVariable(PythonProvisioner.UvMirrorVariable, null);
            StringAssert.StartsWith(PythonProvisioner.UvUrl("uv-x.tar.gz"), "https://github.com/astral-sh/uv/");

            // Trailing slash included, because somebody will paste one.
            Environment.SetEnvironmentVariable(
                PythonProvisioner.UvMirrorVariable, "https://artifactory.corp/github/astral-sh/uv/releases/download/");
            Assert.AreEqual(
                $"https://artifactory.corp/github/astral-sh/uv/releases/download/{PythonProvisioner.UvVersion}/uv-x.tar.gz",
                PythonProvisioner.UvUrl("uv-x.tar.gz"));
        } finally {
            Environment.SetEnvironmentVariable(PythonProvisioner.UvMirrorVariable, saved);
        }
    }

    /// <summary>
    /// A content filter that answers 200 with a block page must not be reported as a
    /// corrupt download — it is the difference between "retry" and "call IT".
    /// </summary>
    [TestMethod]
    public void A_block_page_is_not_mistaken_for_a_checksum() {
        const string digest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        // What GitHub publishes, and what a mirror that reformats publishes. Neither
        // is a network problem, so both have to read the same.
        Assert.AreEqual(digest, PythonProvisioner.ChecksumIn($"{digest}  uv-aarch64-apple-darwin.tar.gz\n"));
        Assert.AreEqual(digest, PythonProvisioner.ChecksumIn($"SHA256 (uv-aarch64-apple-darwin.tar.gz) = {digest}"));

        Assert.IsNull(PythonProvisioner.ChecksumIn("<html><body>Access Denied by Corporate Policy</body></html>"));
        Assert.IsNull(PythonProvisioner.ChecksumIn(new string('a', 63)), "too short");
        Assert.IsNull(PythonProvisioner.ChecksumIn(new string('z', 64)), "right length, not hex");
        Assert.IsNull(PythonProvisioner.ChecksumIn(string.Empty));
    }

    /// <summary>
    /// uv trusts bundled Mozilla roots, not the platform store, so a network that
    /// re-signs TLS breaks it where NuGet works. Recognising that message is what
    /// triggers the retry against the machine's own store.
    /// </summary>
    [TestMethod]
    public void A_certificate_failure_is_told_apart_from_every_other_uv_failure() {
        Assert.IsTrue(PythonProvisioner.LooksLikeCertificateFailure(
            "error sending request: invalid peer certificate: UnknownIssuer"));
        Assert.IsTrue(PythonProvisioner.LooksLikeCertificateFailure(
            "self-signed certificate in certificate chain"));

        Assert.IsFalse(PythonProvisioner.LooksLikeCertificateFailure(
            "No download found for request: cpython-3.99"), "a real uv failure must not retry");
        Assert.IsFalse(PythonProvisioner.LooksLikeCertificateFailure("failed to write to /usr/lib: permission denied"));
    }

    /// <summary>
    /// Packages are per notebook directory and never shared by accident — two
    /// directories called `notebooks` in different repos are the case a bare name
    /// would collide on, which is why the path is hashed into it.
    /// </summary>
    [TestMethod]
    public void Two_notebook_directories_get_two_package_directories() {
        var a = PythonEnvironment.PackagesFor("/tmp/one/notebooks");
        var b = PythonEnvironment.PackagesFor("/tmp/two/notebooks");

        Assert.AreNotEqual(a, b, "same leaf name, different repos");
        Assert.AreEqual(a, PythonEnvironment.PackagesFor("/tmp/one/notebooks"), "stable across sessions");
        StringAssert.Contains(a, "notebooks-", "named for legibility as well as hashed");

        // Nowhere to belong is shared rather than a fresh directory per process,
        // which would leak one for every unsaved buffer ever opened.
        Assert.AreEqual(PythonEnvironment.PackagesFor(null), PythonEnvironment.PackagesFor(" "));
    }

    /// <summary>`requirements.txt` beside the notebook is the definition that belongs
    /// in the repo; the installed bytes never do.</summary>
    [TestMethod]
    public void Requirements_beside_the_notebook_are_found() {
        var directory = Path.Combine(Path.GetTempPath(), "clrkernel-req-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            Assert.IsNull(PythonEnvironment.Requirements(directory), "no file, nothing to install");

            var file = Path.Combine(directory, PythonEnvironment.RequirementsFile);
            File.WriteAllText(file, "cowsay\n");
            CollectionAssert.AreEqual(new[] { "-r", file }, PythonEnvironment.Requirements(directory).ToArray());
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A cell ending on an expression shows it, the way a notebook does; a cell
    /// ending on a statement shows nothing. The split is an AST edit, so it is worth
    /// asserting both halves — getting it wrong either swallows results or prints
    /// `None` after every assignment.
    /// </summary>
    [TestMethod]
    public async Task A_cell_ending_on_an_expression_shows_its_value() {
        using var session = RequirePython();

        Assert.AreEqual("42\n", (await session.ExecuteAsync("41 + 1", null)).Output);
        Assert.AreEqual("'hi'\n", (await session.ExecuteAsync("'hi'", null)).Output, "repr, not str");

        var assignment = await session.ExecuteAsync("y = 1 + 1", null);
        Assert.AreEqual(string.Empty, assignment.Output, "an assignment is not a value");

        var mixed = await session.ExecuteAsync("print('first'); y * 21", null);
        Assert.AreEqual("first\n42\n", mixed.Output, "statements run, then the last expression shows");

        var none = await session.ExecuteAsync("print('only this')", null);
        Assert.AreEqual("only this\n", none.Output, "a call returning None shows nothing extra");

        // What every notebook user reaches for to silence a matplotlib call's return
        // value — and the expression must still be evaluated, only not shown.
        var quiet = await session.ExecuteAsync("z = []\nz.append(1);", null);
        Assert.AreEqual(string.Empty, quiet.Output, "a trailing semicolon suppresses the value");
        Assert.AreEqual("[1]\n", (await session.ExecuteAsync("z", null)).Output, "but it still ran");
    }

    /// <summary>An install cell with nothing to install says so rather than running uv
    /// with no arguments — the message names both ways to give it something.</summary>
    [TestMethod]
    public async Task An_install_with_nothing_named_says_what_to_do() {
        var directory = Path.Combine(Path.GetTempPath(), "clrkernel-noreq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            using var language = new PythonCellLanguage();
            var cell = new CellInvocation("#!python-install", "#!python-install", string.Empty, "#!python-install");
            var e = await Assert.ThrowsExactlyAsync<PythonCellException>(
                () => language.ExecuteAsync(cell, new InstallContext(directory)));

            StringAssert.Contains(e.Message, PythonEnvironment.RequirementsFile);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The cache path has to be somewhere this process can actually write.
    ///
    /// <para>
    /// Not a tautology: `LocalApplicationData` comes back empty in the .NET runtime
    /// container even with HOME set and writable, and combining that with a relative
    /// path produced `/clrkernel` — absolute, well-formed, and denied at the first
    /// download. Writing a file is the only assertion that tells those apart.
    /// </para>
    /// </summary>
    [TestMethod]
    public void The_cache_directory_is_one_this_process_can_write_to() {
        var saved = Environment.GetEnvironmentVariable(PythonInterpreter.HomeVariable);
        try {
            Environment.SetEnvironmentVariable(PythonInterpreter.HomeVariable, null);
            var home = PythonInterpreter.Home();

            Assert.IsTrue(Path.IsPathRooted(home), home);
            var probe = Path.Combine(home, "write-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            Environment.SetEnvironmentVariable(PythonInterpreter.HomeVariable, "/somewhere/else");
            Assert.AreEqual("/somewhere/else", PythonInterpreter.Home(), "the override wins outright");
        } finally {
            Environment.SetEnvironmentVariable(PythonInterpreter.HomeVariable, saved);
        }
    }

    /// <summary>
    /// Every declared directive binds against the shared parser.
    ///
    /// <para>
    /// Studio parses each directive line with it while scanning a notebook for the
    /// connections it names, so a definition that cannot describe its own arguments
    /// is not a Python problem — it threw a FormatException out of an API request
    /// and 500'd the endpoint for a perfectly valid `#!python-install pandas`.
    /// </para>
    /// </summary>
    [TestMethod]
    public void Every_python_directive_binds_the_lines_it_documents() {
        var language = new PythonCellLanguage();
        var install = language.Directives.Single(d => d.Selector == "#!python-install");

        var packages = DirectiveParser.Parse(install, "#!python-install pandas matplotlib==3.9");
        CollectionAssert.AreEqual(new[] { "pandas", "matplotlib==3.9" }, packages.Arguments.ToArray());
        Assert.AreEqual(0, DirectiveParser.Parse(install, "#!python-install").Arguments.Count,
            "bare is legal — it reads requirements.txt");
        CollectionAssert.AreEqual(new[] { "-r", "reqs.txt" },
            DirectiveParser.Parse(install, "#!python-install -r reqs.txt").Arguments.ToArray(),
            "pip's own flags are arguments, not ours to enumerate");

        // The rest take nothing, and should still say so rather than throw on a
        // plain cell body.
        foreach (var directive in language.Directives.Where(d => d.Selector != "#!python-install")) {
            Assert.AreEqual(0, DirectiveParser.Parse(directive, directive.Selector).Arguments.Count);
        }
    }

    /// <summary>
    /// Completion comes from the live namespace, which is the point: a name only
    /// exists once a cell has bound it, and then its real members are offered —
    /// including for a type no static analysis of the cell could have inferred.
    /// </summary>
    [TestMethod]
    public async Task Completion_offers_what_the_session_actually_holds() {
        using var language = RequirePythonLanguage();
        var services = language.Services;

        var before = await services.CompleteAsync("tot", 3, new LanguageServiceContext());
        Assert.IsFalse(before.Items.Any(i => i.Label == "total"), "nothing has bound `total` yet");

        await language.Session.ExecuteAsync("total = 41\nimport json", null);

        var after = await services.CompleteAsync("tot", 3, new LanguageServiceContext());
        Assert.IsTrue(after.Items.Any(i => i.Label == "total"), "bound in a cell, offered in the next");
        Assert.AreEqual(0, after.ReplaceStart);
        Assert.AreEqual(3, after.ReplaceLength, "the prefix is what gets replaced");

        // Members of a real object, not a guess about one.
        var members = await services.CompleteAsync("json.du", 7, new LanguageServiceContext());
        var labels = members.Items.Select(i => i.Label).ToList();
        CollectionAssert.Contains(labels, "dump");
        CollectionAssert.Contains(labels, "dumps");
        Assert.IsFalse(labels.Any(l => l.StartsWith("_")), "privates stay hidden unless asked for");
        Assert.AreEqual(5, members.ReplaceStart, "only `du` is replaced, not `json.du`");

        // Keywords are there too, so an empty session is not an empty list.
        var keywords = await services.CompleteAsync("wh", 2, new LanguageServiceContext());
        Assert.IsTrue(keywords.Items.Any(i => i.Label == "while" && i.Kind == "keyword"));
    }

    /// <summary>
    /// Completing must never *run* the notebook's code. `dir()` on the result of a
    /// call would mean typing a dot executes whatever is to its left.
    /// </summary>
    [TestMethod]
    public async Task Completion_never_calls_the_code_it_completes() {
        using var language = RequirePythonLanguage();
        await language.Session.ExecuteAsync(
            "ran = False\ndef danger():\n    global ran\n    ran = True\n    return 1", null);

        var result = await language.Services.CompleteAsync("danger().", 9, new LanguageServiceContext());
        Assert.AreEqual(0, result.Items.Count, "a call expression offers nothing rather than being evaluated");

        var check = await language.Session.ExecuteAsync("ran", null);
        Assert.AreEqual("False\n", check.Output, "the function was never called");
    }

    /// <summary>Hover and signature help, from the same live objects.</summary>
    [TestMethod]
    public async Task Hover_and_signature_help_read_the_real_object() {
        using var language = RequirePythonLanguage();
        await language.Session.ExecuteAsync("import json", null);

        var hover = await language.Services.HoverAsync("json.dumps", 6);
        StringAssert.Contains(hover.Markdown, "dumps(", "the signature, not just the name");
        Assert.AreEqual(0, hover.Start);
        Assert.AreEqual(10, hover.Length);

        var help = await language.Services.SignatureHelpAsync("json.dumps(x, ", 14);
        Assert.AreEqual(1, help.Signatures.Count);
        StringAssert.Contains(help.Signatures[0].Label, "dumps(");
        Assert.AreEqual(1, help.ActiveParameter, "one comma in, so the second parameter");

        Assert.IsNull(await language.Services.HoverAsync("nosuchname", 4), "an unknown name hovers nothing");
    }

    /// <summary>A language whose interpreter this machine has, or an inconclusive test.</summary>
    private static PythonCellLanguage RequirePythonLanguage() {
        RequirePython().Dispose();
        return new PythonCellLanguage();
    }

    private sealed class InstallContext : ICellExecutionContext {
        public InstallContext(string workingDirectory) => WorkingDirectory = workingDirectory;
        public string WorkingDirectory { get; }
        public Task RunScriptAsync(string code) => Task.CompletedTask;
    }
}
