using System;
using System.Linq;
using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Dax;
using ClrKernel.Language.PowerShell;
using ClrKernel.Language.Shell;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Language.UnitTest;

/// <summary>
/// Goldens written BEFORE the directive parsers moved onto the shared
/// <c>DirectiveParser</c>: every alias spelling, quoting rule, default, and
/// error message a notebook could observe is pinned here, so the refactor is
/// provably behavior-preserving. Complements the per-language tests (SqlTest,
/// DaxTest, SqlEtlTest, PwshRemotingTest, ShellRemotingTest) which pin the
/// common paths.
/// </summary>
[TestClass]
public class DirectiveGoldenTest {

    // ---- #!sql-connect ----

    [TestMethod]
    public void Sql_connect_accepts_every_alias_spelling() {
        var d = SqlDirectives.ParseConnect(
            "#!sql-connect -n c1 -s srv -d db -u usr --secret-ref ref -a sql --trust-server-certificate");
        Assert.AreEqual("c1", d.Spec.Name);
        Assert.AreEqual("srv", d.Spec.Server);
        Assert.AreEqual("db", d.Spec.Database);
        Assert.AreEqual("usr", d.Spec.User);
        Assert.AreEqual("ref", d.Spec.SecretRef);
        Assert.AreEqual(SqlAuthMode.SqlPassword, d.Spec.Auth);
        Assert.IsTrue(d.Spec.TrustServerCertificate);

        var d2 = SqlDirectives.ParseConnect("#!sql-connect --name c2 --host h --username u2 --cs \"Server=x;\"");
        Assert.AreEqual("h", d2.Spec.Server);
        Assert.AreEqual("u2", d2.Spec.User);
        Assert.AreEqual("Server=x;", d2.Spec.RawConnectionString);
    }

    [TestMethod]
    public void Sql_connect_quoting_single_double_and_adjacent_text() {
        var d = SqlDirectives.ParseConnect(
            "#!sql-connect --name 'my conn' --server \"sql server.local\" --database pre\"fix mid\"post");
        Assert.AreEqual("my conn", d.Spec.Name);
        Assert.AreEqual("sql server.local", d.Spec.Server);
        // Quotes glue onto surrounding characters within one token.
        Assert.AreEqual("prefix midpost", d.Spec.Database);
    }

    [TestMethod]
    public void Sql_connect_quoted_empty_string_is_a_real_empty_token() {
        var d = SqlDirectives.ParseConnect("#!sql-connect --name c --server \"\"");
        Assert.AreEqual(string.Empty, d.Spec.Server);
    }

    [TestMethod]
    public void Sql_connect_auth_spellings_all_map() {
        SqlAuthMode Auth(string v) =>
            SqlDirectives.ParseConnect($"#!sql-connect --name c --auth {v}").Spec.Auth;
        Assert.AreEqual(SqlAuthMode.SqlPassword, Auth("sql"));
        Assert.AreEqual(SqlAuthMode.SqlPassword, Auth("sqlpassword"));
        Assert.AreEqual(SqlAuthMode.SqlPassword, Auth("password"));
        Assert.AreEqual(SqlAuthMode.Integrated, Auth("integrated"));
        Assert.AreEqual(SqlAuthMode.Integrated, Auth("windows"));
        Assert.AreEqual(SqlAuthMode.Integrated, Auth("trusted"));
        Assert.AreEqual(SqlAuthMode.AzureAdDefault, Auth("aad"));
        Assert.AreEqual(SqlAuthMode.AzureAdDefault, Auth("entra"));
        Assert.AreEqual(SqlAuthMode.AzureAdDefault, Auth("aad-default"));
        Assert.AreEqual(SqlAuthMode.AzureAdDefault, Auth("entra-default"));
        Assert.AreEqual(SqlAuthMode.AzureAdPassword, Auth("aad-password"));
        Assert.AreEqual(SqlAuthMode.AzureAdPassword, Auth("entra-password"));
        Assert.AreEqual(SqlAuthMode.AzureAdInteractive, Auth("aad-interactive"));
        Assert.AreEqual(SqlAuthMode.AzureAdInteractive, Auth("entra-interactive"));
        Assert.AreEqual(SqlAuthMode.AzureAdInteractive, Auth("interactive"));
        Assert.AreEqual(SqlAuthMode.AzureAdInteractive, Auth("ENTRA-Interactive"), "case-insensitive");

        var e = Assert.ThrowsExactly<FormatException>(() => Auth("kerberos"));
        Assert.AreEqual("Unknown --auth value 'kerberos'.", e.Message);
    }

    [TestMethod]
    public void Sql_connect_auth_defaulting_ladder() {
        // Raw connection string and no user: the string carries its own auth.
        Assert.AreEqual(SqlAuthMode.RawConnectionString,
            SqlDirectives.ParseConnect("#!sql-connect --name c --cs \"Server=x;\"").Spec.Auth);
        // A named user implies a SQL login.
        Assert.AreEqual(SqlAuthMode.SqlPassword,
            SqlDirectives.ParseConnect("#!sql-connect --name c --server s --user u").Spec.Auth);
        // CS plus user: user wins the ladder.
        Assert.AreEqual(SqlAuthMode.SqlPassword,
            SqlDirectives.ParseConnect("#!sql-connect --name c --cs \"Server=x;\" --user u").Spec.Auth);
        // Nothing: Integrated (the spec default).
        Assert.AreEqual(SqlAuthMode.Integrated,
            SqlDirectives.ParseConnect("#!sql-connect --name c --server s").Spec.Auth);
        // Explicit --auth always wins over the ladder.
        Assert.AreEqual(SqlAuthMode.Integrated,
            SqlDirectives.ParseConnect("#!sql-connect --name c --server s --user u --auth integrated").Spec.Auth);
    }

    [TestMethod]
    public void Sql_connect_encrypt_bool_spellings() {
        bool Encrypt(string v) =>
            SqlDirectives.ParseConnect($"#!sql-connect --name c --encrypt {v}").Spec.Encrypt;
        foreach (var v in new[] { "true", "yes", "1", "on", "TRUE" }) {
            Assert.IsTrue(Encrypt(v), v);
        }
        foreach (var v in new[] { "false", "no", "0", "off", "OFF" }) {
            Assert.IsFalse(Encrypt(v), v);
        }
        var e = Assert.ThrowsExactly<FormatException>(() => Encrypt("maybe"));
        Assert.AreEqual("Expected true/false, got 'maybe'.", e.Message);
    }

    [TestMethod]
    public void Sql_connect_options_accumulate_and_validate() {
        var d = SqlDirectives.ParseConnect(
            "#!sql-connect --name c --option \"App Name=My App\" --option MultiSubnetFailover=true");
        Assert.AreEqual("My App", d.Spec.ExtraOptions["App Name"]);
        Assert.AreEqual("true", d.Spec.ExtraOptions["MultiSubnetFailover"]);

        var e = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --name c --option NoEquals"));
        Assert.AreEqual("--option expects key=value, got 'NoEquals'.", e.Message);
        var e2 = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --name c --option =v"));
        Assert.AreEqual("--option expects key=value, got '=v'.", e2.Message);
    }

    [TestMethod]
    public void Sql_connect_error_messages_are_stable() {
        var missing = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --name c --server"));
        Assert.AreEqual("Missing value for --server.", missing.Message);

        var unknown = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --name c --bogus x"));
        Assert.AreEqual("Unknown #!sql-connect flag '--bogus'.", unknown.Message);

        var noName = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --server s"));
        Assert.AreEqual("#!sql-connect requires --name.", noName.Message);

        foreach (var pwFlag in new[] { "--password", "-p" }) {
            var pw = Assert.ThrowsExactly<FormatException>(() =>
                SqlDirectives.ParseConnect($"#!sql-connect --name c {pwFlag} hunter2"));
            StringAssert.Contains(pw.Message, "Passwords must not be placed in notebook cells");
            StringAssert.Contains(pw.Message, "SQL connection panel");
        }
    }

    [TestMethod]
    public void Sql_connect_variable_binding_rules() {
        // Explicit --var (and aliases), validated.
        Assert.AreEqual("db1", SqlDirectives.ParseConnect("#!sql-connect --name c --var db1").Variable);
        Assert.AreEqual("db2", SqlDirectives.ParseConnect("#!sql-connect --name c --variable db2").Variable);
        Assert.AreEqual("db3", SqlDirectives.ParseConnect("#!sql-connect --name c --as db3").Variable);
        var bad = Assert.ThrowsExactly<FormatException>(() =>
            SqlDirectives.ParseConnect("#!sql-connect --name c --var 1abc"));
        Assert.AreEqual("--var '1abc' is not a valid C# identifier.", bad.Message);

        // --no-var suppresses the auto binding.
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name warehouse --no-var").Variable);
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name warehouse --no-variable").Variable);

        // Auto binding: the name when it is a valid, non-keyword identifier.
        Assert.AreEqual("warehouse", SqlDirectives.ParseConnect("#!sql-connect --name warehouse").Variable);
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name \"my db\"").Variable);
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name int").Variable, "C# keyword");
        Assert.AreEqual("_x1", SqlDirectives.ParseConnect("#!sql-connect --name _x1").Variable);
    }

    [TestMethod]
    public void Sql_connect_reference_vs_definition() {
        // Only --name (plus --default / --var / --no-var): a reference to an existing connection.
        Assert.IsTrue(SqlDirectives.ParseConnect("#!sql-connect --name c").IsReference);
        Assert.IsTrue(SqlDirectives.ParseConnect("#!sql-connect --name c --default").IsReference);
        Assert.IsTrue(SqlDirectives.ParseConnect("#!sql-connect --name c --var v").IsReference);
        Assert.IsTrue(SqlDirectives.ParseConnect("#!sql-connect --name c --no-var").IsReference);
        // Any shaping flag makes it a definition.
        Assert.IsFalse(SqlDirectives.ParseConnect("#!sql-connect --name c --server s").IsReference);
        Assert.IsFalse(SqlDirectives.ParseConnect("#!sql-connect --name c --auth integrated").IsReference);
        Assert.IsFalse(SqlDirectives.ParseConnect("#!sql-connect --name c --option a=b").IsReference);
        Assert.IsFalse(SqlDirectives.ParseConnect("#!sql-connect --name c --encrypt true").IsReference);
    }

    [TestMethod]
    public void Sql_connect_default_flag_and_doubled_whitespace() {
        var d = SqlDirectives.ParseConnect("#!sql-connect   --name   c   --server   s   --default");
        Assert.AreEqual("c", d.Spec.Name);
        Assert.AreEqual("s", d.Spec.Server);
        Assert.IsTrue(d.IsDefault);
    }

    // ---- #!sql cell/selector directives ----

    [TestMethod]
    public void Sql_cell_directives_comment_and_inline_forms() {
        Assert.AreEqual("dw", SqlDirectives.SelectorConnection("#!sql --connections dw"));
        Assert.AreEqual("dw", SqlDirectives.SelectorConnection("#!sql --connection dw"));
        Assert.AreEqual("dw", SqlDirectives.SelectorConnection("#!sql -c dw"));
        Assert.IsNull(SqlDirectives.SelectorConnection("#!sql"));

        var r = SqlDirectives.ParseCell("-- connections dw\n-- step load\n-- needs a, b\nSELECT 1");
        Assert.AreEqual("dw", r.ConnectionName);
        Assert.AreEqual("load", r.StepName);
        CollectionAssert.AreEqual(new[] { "a", "b" }, r.Needs.ToArray());

        // Alias + punctuation-stripped forms.
        Assert.AreEqual("dw", SqlDirectives.ParseCell("-- connection: dw\nSELECT 1").ConnectionName);
        CollectionAssert.AreEqual(new[] { "x" },
            SqlDirectives.ParseCell("-- depends-on x\nSELECT 1").Needs.ToArray());

        // The first real SQL line stops the scan.
        Assert.IsNull(SqlDirectives.ParseCell("SELECT 1\n-- connections dw").ConnectionName);
        // Inline selector form wins when present first.
        Assert.AreEqual("a", SqlDirectives.ParseCell("#!sql --connections a\n-- connections b\nSELECT 1").ConnectionName);
    }

    // ---- #!sql-bulk / #!sql-merge / #!sql-run / #!sql-deploy ----

    [TestMethod]
    public void Sql_bulk_flags_defaults_and_errors() {
        var d = SqlEtlDirectives.ParseBulk(
            "#!sql-bulk --from src --from-table dbo.T --table dbo.Dest --batch-size 500 --timeout 90 " +
            "--notify-after 100 --truncate --create --no-lock --keep-identity --keep-nulls --no-progress --map a=b");
        Assert.AreEqual("src", d.FromConnection);
        Assert.AreEqual("src", d.ToConnection, "--to defaults to --from");
        Assert.AreEqual(500, d.Options.BatchSize);
        Assert.AreEqual(90, d.Options.TimeoutSeconds);
        Assert.AreEqual(100, d.Options.NotifyAfter);
        Assert.IsTrue(d.Options.TruncateFirst);
        Assert.IsTrue(d.Options.CreateIfMissing);
        Assert.IsFalse(d.Options.TableLock);
        Assert.IsTrue(d.Options.KeepIdentity);
        Assert.IsTrue(d.Options.KeepNulls);
        Assert.IsFalse(d.Options.ShowProgress);
        Assert.AreEqual("b", d.Options.ColumnMappings["a"]);

        // Aliases.
        var d2 = SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --to-table T -q \"SELECT 1\" --create-if-missing");
        Assert.AreEqual("T", d2.Table);
        Assert.AreEqual("SELECT 1", d2.Query);
        Assert.IsTrue(d2.Options.CreateIfMissing);

        Assert.AreEqual("#!sql-bulk requires --from.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --table T --query q")).Message);
        Assert.AreEqual("#!sql-bulk requires --table.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --query q")).Message);
        Assert.AreEqual("#!sql-bulk requires --query or --from-table.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --table T")).Message);
        Assert.AreEqual("--batch-size expects a number, got 'lots'.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --table T --query q --batch-size lots")).Message);
        Assert.AreEqual("Unknown #!sql-bulk flag '--nope'.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --table T --query q --nope")).Message);
        Assert.AreEqual("--map expects source=dest, got 'ab'.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseBulk("#!sql-bulk --from s --table T --query q --map ab")).Message);
    }

    [TestMethod]
    public void Sql_merge_flags_lists_and_errors() {
        // List values are single tokens: bare Id,Code or a quoted "A , B".
        var d = SqlEtlDirectives.ParseMerge(
            "#!sql-merge -c dw --target dbo.T --source #src --on Id,Code --update A,B --insert \"A , B\" --delete --source-is-query");
        Assert.AreEqual("dw", d.Connection);
        Assert.AreEqual("dbo.T", d.Spec.Target);
        Assert.AreEqual("#src", d.Spec.Source);
        CollectionAssert.AreEqual(new[] { "Id", "Code" }, d.Spec.KeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B" }, d.Spec.UpdateColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B" }, d.Spec.InsertColumns.ToArray());
        Assert.IsTrue(d.Spec.DeleteNotMatchedBySource);
        Assert.IsTrue(d.Spec.SourceIsQuery);

        Assert.AreEqual("#!sql-merge requires --target.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseMerge("#!sql-merge --source s --on k")).Message);
        Assert.AreEqual("#!sql-merge requires --source.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseMerge("#!sql-merge --target t --on k")).Message);
        Assert.AreEqual("#!sql-merge requires --on <key[,key...]>.",
            Assert.ThrowsExactly<FormatException>(() => SqlEtlDirectives.ParseMerge("#!sql-merge --target t --source s")).Message);
    }

    [TestMethod]
    public void Sql_run_and_deploy_flags() {
        var run = SqlOrchestrationDirectives.ParseRun("#!sql-run");
        Assert.IsNull(run.Select);
        Assert.AreEqual(4, run.MaxParallel, "default parallelism");

        var run2 = SqlOrchestrationDirectives.ParseRun("#!sql-run -s load,transform -p 2");
        CollectionAssert.AreEqual(new[] { "load", "transform" }, run2.Select.ToArray());
        Assert.AreEqual(2, run2.MaxParallel);
        Assert.AreEqual("--max-parallel expects a number.",
            Assert.ThrowsExactly<FormatException>(() => SqlOrchestrationDirectives.ParseRun("#!sql-run --max-parallel many")).Message);

        var dep = SqlOrchestrationDirectives.ParseDeploy("#!sql-deploy -c dw --folder ./sql -r --dry-run --no-alter");
        Assert.AreEqual("dw", dep.Connection);
        Assert.AreEqual("./sql", dep.Options.Path);
        Assert.IsTrue(dep.Options.Recurse);
        Assert.IsTrue(dep.Options.DryRun);
        Assert.IsTrue(dep.Options.NoAlter);
        Assert.AreEqual("#!sql-deploy requires --path <folder>.",
            Assert.ThrowsExactly<FormatException>(() => SqlOrchestrationDirectives.ParseDeploy("#!sql-deploy")).Message);
    }

    // ---- #!dax-connect / #!dax ----

    [TestMethod]
    public void Dax_connect_alias_spellings_and_branches() {
        // --dataset is an alias of --model; --aas of --azure-as.
        var fabric = DaxDirectives.ParseConnect("#!dax-connect -n f --fabric --workspace WS --dataset M");
        Assert.AreEqual("powerbi://api.powerbi.com/v1.0/myorg/WS", fabric.Spec.Server);
        Assert.AreEqual(SsasAuthMode.AzureAd, fabric.Spec.Auth);

        // workspace+model without --fabric still selects the fabric branch.
        var implicitFabric = DaxDirectives.ParseConnect("#!dax-connect -n f2 --workspace WS --model M");
        Assert.AreEqual(SsasAuthMode.AzureAd, implicitFabric.Spec.Auth);

        // --integrated flips fabric to the signed-in Windows identity.
        var fabricSspi = DaxDirectives.ParseConnect("#!dax-connect -n f3 --fabric --workspace WS --model M --sspi");
        Assert.AreEqual("powerbi://api.powerbi.com/v1.0/myorg/WS", fabricSspi.Spec.Server);
        Assert.AreNotEqual(SsasAuthMode.AzureAd, fabricSspi.Spec.Auth);

        var aas = DaxDirectives.ParseConnect("#!dax-connect -n a --aas -s asazure://region/srv -d model");
        Assert.AreEqual(SsasAuthMode.AzureAd, aas.Spec.Auth);
        var viaAuth = DaxDirectives.ParseConnect("#!dax-connect -n a2 --auth entra --host srv");
        Assert.AreEqual(SsasAuthMode.AzureAd, viaAuth.Spec.Auth);

        var cs = DaxDirectives.ParseConnect("#!dax-connect -n c --cs \"Data Source=srv;Catalog=m;\"");
        Assert.AreEqual(SsasAuthMode.ConnectionString, cs.Spec.Auth);

        Assert.AreEqual("#!dax-connect --fabric requires --workspace and --model.",
            Assert.ThrowsExactly<FormatException>(() =>
                DaxDirectives.ParseConnect("#!dax-connect -n f --fabric --workspace WS")).Message);
        Assert.AreEqual("#!dax-connect requires --server (or --connection-string / --fabric).",
            Assert.ThrowsExactly<FormatException>(() =>
                DaxDirectives.ParseConnect("#!dax-connect -n x --auth entra")).Message);
        Assert.AreEqual("Unknown #!dax-connect flag '--bogus'.",
            Assert.ThrowsExactly<FormatException>(() =>
                DaxDirectives.ParseConnect("#!dax-connect -n x --bogus")).Message);
    }

    [TestMethod]
    public void Dax_secret_ref_pre_parse_scan() {
        Assert.AreEqual("dax:sales",
            DaxDirectives.SecretRefOf("#!dax-connect --name c --server s --user u --secret dax:sales"));
        Assert.AreEqual("r", DaxDirectives.SecretRefOf("#!dax-connect --secret-ref r"));
        Assert.IsNull(DaxDirectives.SecretRefOf("#!dax-connect --name c --server s"));
    }

    [TestMethod]
    public void Dax_cell_selector_and_comment_forms() {
        Assert.AreEqual("sales", DaxDirectives.SelectorConnection("#!dax --connections sales"));
        Assert.AreEqual("sales", DaxDirectives.SelectorConnection("#!dax --cube sales"));
        Assert.AreEqual("sales", DaxDirectives.SelectorConnection("#!dax -c sales"));

        Assert.AreEqual("sales", DaxDirectives.ParseCell("-- cube sales\nEVALUATE T").CubeName);
        Assert.AreEqual("sales", DaxDirectives.ParseCell("// connections: sales\nEVALUATE T").CubeName);
        Assert.IsNull(DaxDirectives.ParseCell("EVALUATE T\n-- cube sales").CubeName, "first DAX line stops the scan");
    }

    // ---- #!pwsh-connect / #!shell-connect ----

    [TestMethod]
    public void Pwsh_connect_aliases_transport_and_errors() {
        var ssh = PwshDirectives.ParseConnect("#!pwsh-connect -n box --computer host1 -u me --port 2222 -i '/my key/id_rsa'");
        Assert.AreEqual("box", ssh.Name);
        Assert.AreEqual("host1", ssh.Host);
        Assert.AreEqual("me", ssh.User);
        Assert.AreEqual(2222, ssh.Port);
        Assert.AreEqual("/my key/id_rsa", ssh.IdentityFile);
        Assert.AreEqual(PwshTransport.Ssh, ssh.Transport, "ssh is the default transport");

        var winrm = PwshDirectives.ParseConnect("#!pwsh-connect --name w --server host2 --wsman --username svc --secret-ref pw --ssl");
        Assert.AreEqual(PwshTransport.WinRm, winrm.Transport);
        Assert.AreEqual("pw", winrm.SecretRef);
        Assert.IsTrue(winrm.UseSsl);

        Assert.AreEqual("--port expects a number.",
            Assert.ThrowsExactly<FormatException>(() => PwshDirectives.ParseConnect("#!pwsh-connect -n b --host h --port abc")).Message);
        Assert.AreEqual("#!pwsh-connect requires --name.",
            Assert.ThrowsExactly<FormatException>(() => PwshDirectives.ParseConnect("#!pwsh-connect --host h")).Message);
        Assert.AreEqual("#!pwsh-connect requires --host.",
            Assert.ThrowsExactly<FormatException>(() => PwshDirectives.ParseConnect("#!pwsh-connect --name b")).Message);
        StringAssert.Contains(
            Assert.ThrowsExactly<FormatException>(() => PwshDirectives.ParseConnect("#!pwsh-connect -n b --host h --password x")).Message,
            "Passwords must not be placed in notebook cells");
        Assert.AreEqual("Unknown #!pwsh-connect flag '--nope'.",
            Assert.ThrowsExactly<FormatException>(() => PwshDirectives.ParseConnect("#!pwsh-connect -n b --host h --nope")).Message);
    }

    [TestMethod]
    public void Shell_connect_aliases_and_errors() {
        var s = ShellDirectives.ParseConnect("#!shell-connect -n build -h build01 -u deploy -p 22 -i ~/.ssh/id_ed25519 --shell ZSH");
        Assert.AreEqual("build", s.Name);
        Assert.AreEqual("build01", s.Host);
        Assert.AreEqual("deploy", s.User);
        Assert.AreEqual(22, s.Port);
        Assert.AreEqual("~/.ssh/id_ed25519", s.IdentityFile);
        Assert.AreEqual("zsh", s.RemoteShell, "--remote-shell value is lowercased");

        StringAssert.Contains(
            Assert.ThrowsExactly<FormatException>(() => ShellDirectives.ParseConnect("#!shell-connect -n b -h h --password x")).Message,
            "key authentication");
        Assert.AreEqual("Unknown #!shell-connect flag '--nope'.",
            Assert.ThrowsExactly<FormatException>(() => ShellDirectives.ParseConnect("#!shell-connect -n b -h h --nope")).Message);
    }

    [TestMethod]
    public void Pwsh_and_shell_selector_connection_extraction() {
        Assert.AreEqual("box", PwshDirectives.SelectorConnection("#!pwsh --connection box"));
        Assert.IsNull(PwshDirectives.SelectorConnection("#!pwsh"));
        Assert.AreEqual("box", ShellDirectives.SelectorConnection("#!bash --connection box"));
        Assert.IsNull(ShellDirectives.SelectorConnection("#!zsh"));
    }
}
