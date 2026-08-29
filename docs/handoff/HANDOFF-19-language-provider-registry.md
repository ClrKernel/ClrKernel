# HANDOFF-19 — The Language Provider Registry (extensible cell languages, kernel 0.10)

## Why

Language knowledge had been re-typed by hand in four places and had already drifted:

- **language-tag maps** in four copies, two divergent — the worst consequence being that
  ```sql / ```dax / ```bash tags in a `.nb.md` ran in VS Code but were **silently prose**
  for `clrkernel run` and every ClrKernel.Studio job (`NotebookDocument` only knew
  csharp/http/mermaid/pwsh).
- **Directive flags** in four encodings: the parsers (truth), the completion tables
  (`#!sql-connect` completion omitted `--provider/--option/--var/--no-var`; DAX omitted
  `--integrated` while the extension emitted it), the extension's `directives.ts` builders, and
  two ~490-line hand-written connection wizards.
- **Four hand-written tokenizers** with two different empty-token semantics, six copies of the
  same parse loop.
- **No provider discovery**: `$type` → provider was a hard-coded const per session; `#r`-loaded
  Oracle/Odbc/Jdbc were invisible to every catalog and RPC; serve's `initialize` returned a
  hard-coded, wrong `languages: ["csharp"]`.

The architect's direction: a registry of self-describing language providers — directives with
standardized parsing supplied by Core.Scripting, connection providers with per-`$type` settings
schemas — and front ends that know nothing about specific languages.

## What landed (one commit per phase on `feature/language-provider-registry`)

1. **`refactor(directives)`** — `DirectiveDefinition`/`DirectiveParameter` (declarative flag
   tables) + `DirectiveParser` (the one quote-aware, empty-token-preserving tokenizer and
   binder) in Core.Scripting. All six parsers rebuilt on it; semantic ladders (auth defaulting,
   DAX's provider-factory dispatch, int/bool conversion) stayed in the languages so their exact
   error messages survive — pinned by goldens written **before** the refactor
   (`DirectiveGoldenTest`). Deliberate fix: pwsh/shell per-cell `--connection` moved off a regex,
   so quoted names work.
2. **`feat(languages)` (self-description)** — `ICellLanguage` gains `DisplayName`,
   `LanguageTags`, `Directives` (default interface members; toys unaffected).
   `LanguageDescriptor` is the one wire shape; `CellLanguageSet.Describe()` produces it.
   `DirectiveCompletion` generates magic-line completion AND directive-line diagnostics from the
   same tables the parsers bind — the drifted `_magicFlags` copies are gone, and a bad flag is
   an editor diagnostic instead of a run-time `FormatException`.
3. **`feat(languages)` (tag unification + RPC)** — `NotebookDocument`/`NotebookImporter` parse
   by descriptor list; no descriptors degrades to C#-only. `clrkernel run` passes the registry;
   **Jobs initializes the kernel before parsing** and uses the descriptors from the initialize
   reply, so a job executes exactly the tags its kernel can run (verified live: a bash tag
   executes headlessly). serve `initialize` returns the full list; lsp carries
   `capabilities.experimental.clrkernel.languages` + a `clrkernel/languages` request. serve now
   speaks the same camelCase System.Text.Json wire as lsp. Kernel 0.9.x → **0.10.0**.
4. **`feat(providers)`** — `ConnectionProviderDescriptor` (contract in **Core.Primitives**, so
   opt-in provider packages never depend on the scripting stack): `$type`, languages, connect
   selector, and per-setting schema (canonical key + read-side aliases owned here, kind,
   one-of groups, enum values, defaults, directive flag, `RuntimeOnly` for the non-serializable
   SSAS token provider / Fabric runtime endpoint). Descriptors for SqlServer, AnalysisServices,
   Fabric, Oracle, Odbc, Jdbc, Ssh, PSRemoting. Lookup is per-language
   (`engine.ConnectionProvidersFor`) — never `$type`→one-provider, because `Ssh` serves both
   shellscript and powershell. Served via lsp `clrkernel/connections/describe` and serve
   `describeConnections`. Drift-guard tests pin every config key each `FromConfig`/`FromNode`
   reads to its descriptor.
5. **`feat(plugins)`** — `[assembly: CellLanguageExport(...)]` (Core.Scripting) and
   `[assembly: ConnectionProviderExport(...)]` (Core.Primitives). The engine scans every
   assembly a `#r` brings in (nuget-resolved and direct dll paths), loads it in the **default
   ALC** (contract-type identity; the isolated cell-library context is not for plugins), and
   registers exports with that session only. Duplicate Ids/Types are skipped — the shipped
   assemblies carry the same exports as worked examples. Engines raise `LanguagesChanged`;
   hosts notify `clrkernel/languagesChanged` / `languagesChanged`.
6. **`feat(vscode)` (descriptor consumption)** — `src/languages.ts` holds the live list
   (handshake + change notification) with `bundledLanguages` as the pre-handshake / old-kernel
   fallback. Picker, `cellCode()` selector prepending, config auto-load, serializer tag maps
   (both directions), and the LSP `documentSelector` (from the new `hasEditorServices` field —
   powershell cells get LSP features now) are all derived. Pairing bumped to 0.10.x.
7. **`feat(vscode)` (generic wizard)** — `connections.ts` + `connectionDirective.ts` replace
   `sqlConnections.ts`/`daxConnections.ts` (net −457 lines): one status-bar button and one
   schema-rendered wizard for every `hasConnections` language. Secrets ride the RPC `secret`
   parameter only — asserted never to appear in the composed line. Enums serialize as camelCase
   strings on the wire (`JsonStringEnumConverter` on both hosts and the Jobs client).

## Decisions and bounds

- **Presentation is VSIX-static.** A runtime-plugged language gets execution, routing, tags,
  completion, and the connection UI — but `contributes.languages/grammars` can't be served, so
  no highlighting/icon without a companion extension. Stated in CLAUDE.md.
- **Serializer-before-server**: a `.nb.md` opened before the kernel starts uses the bundled
  tag map; only runtime-plugged languages' tags deserialize as markup until the kernel is
  up. The `documentSelector` is fixed at client construction — plugin languages gain editor
  features on the next server start.
- **Jupyter is out of scope, unchanged**: `LanguageRequestHandler` still routes everything to
  the C# `ScriptLanguageService` and `LanguageInfo` is hard-coded C#. Pre-existing asymmetry,
  small independent fix (route through `Languages.ById(...)?.Services`) for later. Plugin
  *execution* in Jupyter already works — same engine.
- **Jdbc has no `$type`/connections.json backing** — descriptor + code-configured only; adding
  a config path is a noted follow-up. Oracle/Odbc keep their eager-secret `FromConfig` (the
  lazy `RawConnectionNode` unification wasn't forced by anything this round).
- **PostgreSQL**: no provider built (deliberate) — it is reachable via ODBC/JDBC, and the
  descriptor model was confirmed to accommodate a future Npgsql provider (plain
  Text/Enum/SecretRef settings).
- Known pre-existing failures in `test/tools/lsp_harness.py` (member completion / hover on an
  executed variable) reproduce identically against `main` — they belong to the LSP
  session-keying work on the separate `feature/display-formatters` branch, not to this one.
  Every check added this round (descriptor handshake, `clrkernel/languages`,
  `connections/describe` schemas) passes.

## Where to look

| Concern | File |
|---|---|
| Directive tables + parser | `src/ClrKernel.Core.Scripting/DirectiveDefinition.cs`, `DirectiveParser.cs` |
| Generated completion/diagnostics | `src/ClrKernel.Core.Scripting/DirectiveCompletion.cs` |
| Language wire shape | `src/ClrKernel.Core.Scripting/LanguageDescriptor.cs` |
| Provider contract | `src/ClrKernel.Core.Primitives/ConnectionProviderDescriptor.cs` |
| Plugin exports + engine hook | `PluginExports.cs`, `ConnectionProviderExport.cs`, `InteractiveScriptEngine.RegisterPlugins` |
| Extension language model | `editors/vscode/src/languages.ts` |
| Generic wizard | `editors/vscode/src/connections.ts`, `connectionDirective.ts` |
| Behavior pins | `DirectiveGoldenTest`, `LanguageDescriptorTest`, `PluginRegistrationTest`, `ConnectionProviderDescriptorTest` |
