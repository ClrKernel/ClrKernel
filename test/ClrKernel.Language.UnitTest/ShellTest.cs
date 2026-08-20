using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class ShellTest {
    private static InteractiveScriptEngine NewEngine() =>
        new(Directory.GetCurrentDirectory(), NullLogger.Instance);

    // Windows agents may not have bash; every test skips rather than fails there.
    private static void RequireShell(string shell) {
        try {
            var session = new ShellSession();
            session.ExecuteAsync(shell, "exit 0", null).GetAwaiter().GetResult();
        } catch (ShellCellException) {
            Assert.Inconclusive($"{shell} is not available on this machine.");
        }
    }

    private static string Text(object result) =>
        result is DisplayData d && d.Data.TryGetValue("text/plain", out var t) ? t?.ToString() : null;

    private static string Html(object result) =>
        result is DisplayData d && d.Data.TryGetValue("text/html", out var h) ? h?.ToString() : null;

    [TestMethod]
    public async Task Bash_cell_output_comes_back() {
        RequireShell("bash");
        var result = await NewEngine().ExecuteAsync("#!bash\necho hello from bash");
        Assert.AreEqual("hello from bash", Text(result));
    }

    [TestMethod]
    public async Task Ansi_colour_renders_as_html_and_strips_from_text() {
        RequireShell("bash");
        var result = await NewEngine().ExecuteAsync("#!bash\nprintf '\\033[31mred\\033[0m\\n'");
        Assert.AreEqual("red", Text(result), "escapes must be stripped from text/plain");
        StringAssert.Contains(Html(result), "ansi", "escapes must render as colour spans");
    }

    [TestMethod]
    public async Task Colour_is_forced_even_though_output_is_a_pipe() {
        RequireShell("bash");
        // Not a TTY, so tools decide by convention: the session must advertise one.
        var result = await NewEngine().ExecuteAsync("#!bash\necho \"$TERM/$CLICOLOR_FORCE/$FORCE_COLOR\"");
        Assert.AreEqual("xterm-256color/1/1", Text(result));
    }

    [TestMethod]
    public async Task Working_directory_persists_across_cells() {
        RequireShell("bash");
        var engine = NewEngine();
        var dir = Path.Combine(Path.GetTempPath(), "ck-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            await engine.ExecuteAsync($"#!bash\ncd '{dir}'");
            var pwd = Text(await engine.ExecuteAsync("#!bash\npwd"));
            StringAssert.Contains(pwd, Path.GetFileName(dir), "cd in one cell must hold in the next");
        } finally {
            try { Directory.Delete(dir); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task Exported_environment_persists_across_cells() {
        RequireShell("bash");
        var engine = NewEngine();
        await engine.ExecuteAsync("#!bash\nexport CK_SHELL_TEST=fourty-two");
        Assert.AreEqual("fourty-two", Text(await engine.ExecuteAsync("#!bash\necho $CK_SHELL_TEST")));
    }

    [TestMethod]
    public async Task Stderr_is_captured_with_stdout() {
        RequireShell("bash");
        var result = await NewEngine().ExecuteAsync("#!bash\necho to-stderr 1>&2");
        Assert.AreEqual("to-stderr", Text(result));
    }

    [TestMethod]
    public async Task A_nonzero_exit_fails_the_cell_but_still_shows_the_output() {
        RequireShell("bash");
        DisplayData shown = null;
        void OnCell(ClrKernel.Core.Primitives.DisplayCell cell) => shown = MimeBundler.Bundle(cell);
        ClrKernel.Core.Primitives.DisplayValues.OnCellDisplayed += OnCell;
        try {
            var e = await Assert.ThrowsExactlyAsync<ShellCellException>(
                () => NewEngine().ExecuteAsync("#!bash\necho about to fail\nexit 3"));
            StringAssert.Contains(e.Message, "3");
            Assert.IsNotNull(shown, "the output before the failure must still be displayed");
            Assert.AreEqual("about to fail", shown.Data["text/plain"]);
        } finally {
            ClrKernel.Core.Primitives.DisplayValues.OnCellDisplayed -= OnCell;
        }
    }

    [TestMethod]
    public async Task Sh_selector_uses_sh() {
        RequireShell("sh");
        var result = await NewEngine().ExecuteAsync("#!sh\necho via sh");
        Assert.AreEqual("via sh", Text(result));
    }

    [TestMethod]
    public async Task Zsh_selector_uses_zsh() {
        RequireShell("zsh");
        var result = await NewEngine().ExecuteAsync("#!zsh\necho -n $ZSH_VERSION");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Text(result)), "ZSH_VERSION only exists inside zsh");
    }

    [TestMethod]
    public void Markdown_shell_tags_become_selector_blocks() {
        var md = "```bash\necho a\n```\n\n```zsh\necho b\n```\n\n```sh\necho c\n```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(3, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!bash\n");
        StringAssert.StartsWith(blocks[1], "#!zsh\n");
        StringAssert.StartsWith(blocks[2], "#!sh\n");
    }
}
