# Changelog

## [0.2.0] - 2026-08-07
- The notebook server now ships inside the `ClrKernel` CLI tool and is launched
  as `clrkernel serve` (the standalone `ClrKernel.Server` dotnet tool is gone).
- Auto-install now installs the `ClrKernel` global tool
  (`dotnet tool install --global ClrKernel`).
- Default settings updated: `clrkernel.server.command` is now `clrkernel` and
  `clrkernel.server.args` defaults to `["serve"]`. If you previously overrode
  these to point at `ClrKernel.Server`, update them to `clrkernel`/`serve` (or
  `dotnet` with `["<path>/ClrKernel.dll", "serve"]` for a dev build).

## [0.1.1] - 2026-08-07
- Setting up automatic publish extension

## [0.1.0] - 2026-08-06

Initial release.

- Executable markdown notebooks (`*.nb.md`): fenced `csharp` blocks as code
  cells, prose as markdown cells, clean round-trip serialization.
- **ClrKernel C#** notebook controller executing through `ClrKernel.Server`
  (JSON-RPC over stdio) on the ClrKernel.Core Roslyn engine.
- REPL state across cells; `#r "nuget: ..."` package references;
  `#!import`/`#!lib` shared libraries with prefixes and run-once semantics.
- Streaming console output and in-place display updates (`DisplayAs`/`Update`).
- Configurable server launch (`clrkernel.server.command` / `.args`) with clear
  startup diagnostics in the ClrKernel output channel.
