# Changelog

## 0.1.0

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
