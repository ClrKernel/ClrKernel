# Changelog

## [0.4.0] - 2026-08-08
- HTTP request cells. Set a cell's language to **HTTP** (or start it with the
  `#!http` selector) and write requests in the VS Code REST Client `.http`
  syntax — variables, system variables (`{{$guid}}`, `{{$timestamp}}`, …),
  `###`-separated multiple requests, and request chaining
  (`{{login.response.body.$.token}}`). Each request renders a rich response
  card: color-coded status, timing and size, collapsible headers, and a
  pretty-printed, highlighted JSON body.
- Executable markdown now round-trips ` ```http ` fenced blocks as HTTP cells,
  alongside the existing `csharp` blocks.
- Syntax highlighting for HTTP cells (methods, URLs, headers, comments, `###`
  separators, `@variables`, `{{…}}` interpolation, and an embedded JSON body).
- Mermaid diagram cells. Set a cell's language to **Mermaid** (or start it with
  the `#!mermaid` selector) and write Mermaid syntax — flowcharts, sequence,
  class, state, ER, gantt, pie, and more. Diagrams render **fully offline** (the
  Mermaid library is embedded — no CDN) and follow the editor's light/dark theme.
- Executable markdown now round-trips ` ```mermaid ` fenced blocks as diagram
  cells, alongside the existing `csharp` blocks.
- Syntax highlighting for Mermaid cells (diagram keywords, directions, arrows,
  node labels, `%%` comments, strings).
- PowerShell cells. Set a cell's language to **PowerShell** (or start it with the
  `#!pwsh` selector) and run PowerShell in an in-process runspace: variables,
  functions, and imported modules persist across cells, and output is formatted
  the way the console shows it. Self-contained — no separate PowerShell install
  needed.
- **PowerShell IntelliSense** — native completion, hover, and signature help for
  cmdlets, parameters, provider paths, and session-defined variables/functions,
  served from the live runspace over the same language server.
- Syntax highlighting uses VS Code's built-in PowerShell grammar.
- Executable markdown round-trips ` ```powershell ` fenced blocks as PowerShell
  cells, alongside the existing `csharp` blocks.

## [0.3.0] - 2026-08-07
- C# IntelliSense in notebook cells — completion, hover, and signature help —
  with no C# Dev Kit required. Powered by a built-in language server that shares
  the execution engine, so completions reflect the live session: variables from
  executed cells, `#r "nuget:"` types, and imports.
- The extension now launches `clrkernel lsp` (a unified language server) and
  carries execution + language features over one connection. Default
  `clrkernel.server.args` is now `["lsp"]`; a dev build uses
  `dotnet` + `["<path>/ClrKernel.dll", "lsp"]`.

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
