using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Npm.NpmTasks;

/// <summary>
/// ClrKernel developer task runner (Nuke). Run <c>./build.sh --help</c>
/// (macOS/Linux) or <c>.\build.ps1 --help</c> (Windows) to list targets and
/// parameters. Examples:
///   ./build.sh                       # restore, build, and test the solution
///   ./build.sh Build                 # build the whole solution
///   ./build.sh Test                  # run all unit tests
///   ./build.sh Build --project ClrKernel.Language.Http   # build a single project
///   ./build.sh Test  --filter Http              # run a subset of tests
///   ./build.sh Extension             # build the VS Code extension
///   ./build.sh ExtensionTest         # run the extension's unit tests
///   ./build.sh All                   # solution build+test AND the extension
///   ./build.sh Format                # verify formatting (Format --apply to fix)
/// </summary>
class ClrKernelBuild : NukeBuild {
    public static int Main() => Execute<ClrKernelBuild>(x => x.Test);

    [Parameter("Build configuration — Debug or Release (default: Release).")]
    readonly string Configuration = "Release";

    [Parameter("Limit Build/Restore to a single project by name, e.g. --project ClrKernel.Language.Http " +
               "(searches src/ then test/). Omit for the whole solution.")]
    readonly string Project;

    [Parameter("Filter passed to 'dotnet test --filter', e.g. --filter Http or --filter Name~Mermaid.")]
    readonly string Filter;

    [Parameter("For the Format target: apply fixes instead of only verifying.")]
    readonly bool Apply;

    AbsolutePath SolutionFile => RootDirectory / "ClrKernel.slnx";
    AbsolutePath ExtensionDirectory => RootDirectory / "editors" / "vscode";

    // The build/restore target: a single --project if given, else the solution.
    AbsolutePath TargetFile => string.IsNullOrEmpty(Project) ? SolutionFile : ResolveProject(Project);

    AbsolutePath ResolveProject(string name) {
        var candidates = new[] {
            RootDirectory / "src" / name / (name + ".csproj"),
            RootDirectory / "test" / name / (name + ".csproj"),
        };
        var found = candidates.FirstOrDefault(p => p.FileExists());
        if (found == null) {
            throw new Exception($"Project '{name}' not found under src/ or test/. " +
                $"Expected one of: {string.Join(", ", candidates.Select(p => p.ToString()))}");
        }
        return found;
    }

    Target Clean => _ => _
        .Description("Delete build outputs (bin/obj across the repo and the extension's out/).")
        .Executes(() => {
            foreach (var dir in RootDirectory.GlobDirectories(
                "src/**/bin", "src/**/obj", "test/**/bin", "test/**/obj", "build/bin", "build/obj")) {
                dir.DeleteDirectory();
            }
            (ExtensionDirectory / "out").DeleteDirectory();
        });

    Target Restore => _ => _
        .Description("Restore NuGet packages (whole solution, or a single --project).")
        .Executes(() => {
            DotNetRestore(s => s.SetProjectFile(TargetFile));
        });

    Target Build => _ => _
        .Description("Build the whole solution, or a single --project.")
        .DependsOn(Restore)
        .Executes(() => {
            DotNetBuild(s => s
                .SetProjectFile(TargetFile)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .Description("Run unit tests (all, or a subset with --filter <expr>).")
        .DependsOn(Build)
        .Executes(() => {
            // The suite is three projects since P9 (Core / Language / Database), so Test runs the
            // whole solution instead of a fixed test project. --project still narrows to one, and
            // --filter behaves exactly as it did when there was a single test assembly.
            DotNetTest(s => {
                s = s.SetProjectFile(string.IsNullOrEmpty(Project) ? SolutionFile : TargetFile)
                    .SetConfiguration(Configuration)
                    .EnableNoRestore();
                if (!string.IsNullOrEmpty(Filter)) {
                    s = s.SetFilter(Filter);
                }
                return s;
            });
        });

    Target Format => _ => _
        .Description("Verify code formatting; pass --apply to fix in place.")
        .Executes(() => {
            // dotnet format resolves the .slnx from the repo root; run there.
            // Plain (non-interpolated) strings so Nuke doesn't auto-quote args.
            var args = Apply ? "format ClrKernel.slnx" : "format ClrKernel.slnx --verify-no-changes";
            DotNet(args, workingDirectory: RootDirectory);
        });

    Target Extension => _ => _
        .Description("Build the VS Code extension (npm install + compile in editors/vscode).")
        .Executes(() => {
            Npm("install", ExtensionDirectory);
            Npm("run compile", ExtensionDirectory);
        });

    Target ExtensionTest => _ => _
        .Description("Run the VS Code extension's unit tests (vitest).")
        .DependsOn(Extension)
        .Executes(() => {
            // The extension is TypeScript that tsc alone can only prove type-correct. These cover
            // the parts with real logic: the directives sent to the kernel, the version guard, and
            // the .nb.md serializer.
            Npm("test", ExtensionDirectory);
        });

    Target All => _ => _
        .Description("Build and test the solution AND build and test the VS Code extension.")
        .DependsOn(Test, ExtensionTest);
}
