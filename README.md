# ClrKernel

A Jupyter kernel for .NET. C# cells are evaluated with Roslyn's scripting engine
([Microsoft.CodeAnalysis.CSharp.Scripting](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting)),
with more CLR languages (PowerShell, F#) on the roadmap. Notebooks run
interactively in JupyterLab / VS Code (Jupyter extension) and headlessly via
`nbconvert` or `papermill` — including from schedulers like SQL Server Agent.

ClrKernel is a maintained fork of
[SciSharp/ICSharpCore](https://github.com/SciSharp/ICSharpCore), created after
Microsoft deprecated .NET Interactive / Polyglot Notebooks (April 2026).
Relative to upstream it adds: correct headless execution under
nbclient/papermill, full output capture for `async`/`await` cells, control
channel + heartbeat + graceful `shutdown_request` handling, patched vulnerable
dependencies, and the kernelspec shipped inside the NuGet package.

## Install

```bash
dotnet tool install --global ClrKernel
jupyter kernelspec install "$(clrkernel --kernel-spec-path)" --user --name clrkernel
jupyter kernelspec list   # should show: clrkernel
```

Requires a .NET 8+ runtime (`RollForward=Major`: newer majors work) and Jupyter.

## Use

Pick the **ClrKernel (C#)** kernel in JupyterLab or VS Code. Cells support
`#r "nuget: Package, Version"` and `#r "path/to/local.dll"` references, with
REPL-style state persisting across cells.

Headless / scheduled execution:

```bash
jupyter nbconvert --to notebook --execute --output out.ipynb etl.ipynb
papermill etl.ipynb runs/etl_out.ipynb -k clrkernel --language .net-csharp -p run_date 2026-08-04
```

A failing cell exits non-zero (job schedulers see the failure); papermill also
persists the partially-executed output notebook as a diagnostic artifact.

## Develop

```bash
./scripts/install-dev-kernel.sh    # kernel 'clrkernel-dev' running from bin/ output
                                   # iterate: dotnet build + restart kernel
./scripts/install-local-tool.sh    # pack + install the global tool from a local
                                   # feed; tests the full packaged experience
clrkernel --kernel-spec-details    # show which kernelspec the binary resolves
```

## License

Apache 2.0, preserving the upstream license. Original work © SciSharp
(Kerry Jiang, Haiping Chen, and contributors); fork maintained by
[ClrKernel](https://github.com/ClrKernel).
