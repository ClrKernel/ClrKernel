# HANDOFF-18 — Pluggable display formatting (`DisplayFormatters`)

**Branch:** `feature/display-formatters` (off main after the DAX credential-store work).
**Status:** approved plan, in progress. The checklist at the bottom is the resume point.

## The problem

Two divergent display paths produce different output for the same value:

- `Display()` / `DisplayAs()` / `DisplayTable()` (`Core.Primitives/DisplayExtensions.cs`) emit
  `display_data` — `Display()` is just `ToString()` into one mime type.
- A trailing cell expression goes through `ResultFormatter.Format`
  (`InteractiveScriptEngine`), which renders rich HTML (property tables, sequences).

So `x.Display()` looks worse than a bare trailing `x`, and a trailing `x.Display()` shows the
value *and then* the handle. Languages also build HTML strings by hand (`SqlSession`,
`SsasConnection`, `HttpResponseRenderer`, `PowerShellSession`, …) and push them through
`new DisplayData(text, html)` — rendering is welded into every producer.

## The design (approved)

**Concepts live in `ClrKernel.Core.Primitives`; every render implementation is a registered
formatter living outside Primitives.** A formatter converts one display concept to another:

```
DisplayFormatter  = (Type InputType, Type OutputType, Func<IDisplayValue,IDisplayValue> Format)
DisplayFormatters = static registry: Register/Unregister/Format<T>/Find
                    exact match first, then one 2-hop chain (A→B→C)
```

Concept records (all `IDisplayValue { object Value }`):

| concept | payload |
|---|---|
| `DisplayObject` | raw object + optional `PreferredDisplayType` / `PreferredMimeType` |
| `DisplayTable` | columns / rows / types / total (already-shaped tabular data) |
| `DisplayConsoleText` | console text, may contain ANSI escapes |
| `DisplayText` | plain text |
| `DisplayHtml` | html string |
| `DisplayMarkdown` | markdown string |
| `DisplayProgress` | label / status / completed / total |
| `DisplayBytes` | `byte[]` + mime (images, pdfs, …) |

`DisplayValues` (extension methods on `object`) creates a `DisplayCell(displayId)` per call;
`cell.Update(value, preferredType?, preferredMime?)` re-renders in place. Static events
`OnCellDisplayed` / `OnCellUpdated` / `OnCellDisplayError` observe the flow. `DisplayCell` is a
handle **structure — it is never itself rendered**; the engine suppresses it (and legacy
`DisplayedValue`) as a trailing value.

Packaging (concept → `DisplayData` mime bundle) stays in Primitives — it decides *which* mimes
to ask the registry for, it renders nothing itself:

- `text/plain` ← `Format<DisplayText>` (fallback: `Value.ToString()`)
- `text/html` ← `Format<DisplayHtml>` (skipped when no formatter chain exists)
- `DisplayBytes` → base64 under its own mime; `DisplayMarkdown` → `text/markdown` passthrough
- `DisplayObject.PreferredDisplayType` is resolved first (convert to the preferred concept via
  the registry, then to the requested output)

`DisplayCell` captures the emit/update handlers at creation (like `DisplayedValue` did), so
background updates keep flowing to the originating cell's output.

### The plugin package: `ClrKernel.Formatting.Html`

References only Primitives. Holds every HTML render, registered at the composition root
(`src/ClrKernel/CellLanguages.cs` neighborhood) and mirrored in the test `[AssemblyInitialize]`:

| registration | code that becomes it |
|---|---|
| `DisplayObject → DisplayHtml` / `→ DisplayText` | `ResultFormatter` (rich render kept) |
| `DisplayObject → DisplayTable` | reflection / IDataReader / DataTable extraction |
| `DisplayTable → DisplayHtml` | `InteractiveTable` (grid, filters, Analyze) |
| `DisplayConsoleText → DisplayHtml` | `AnsiRenderer.ToHtml` (`→ DisplayText` = `Strip`) |
| `DisplayProgress → DisplayHtml` | `ProgressBar` |
| `DisplayMarkdown → DisplayHtml` | trivial `<pre>`-free passthrough render |

Users override from a cell: `DisplayFormatters.Register<DisplayTable, DisplayHtml>(t => ...)`
(last registration wins — `Find` scans newest first). The registry is static/process-wide;
serve/lsp host one engine per window today, so per-engine plumbing is deliberately skipped.

Languages **produce concepts, never HTML**: SQL/DAX build `DisplayTable` from their readers,
PowerShell returns `DisplayConsoleText`, HTTP composes concepts, Mermaid may keep emitting
`DisplayHtml` (its entire job is a render — legal in this model).

### Decisions made

- Package name: **`ClrKernel.Formatting.Html`** (fourth small tier, `Formatting.*`;
  a future markdown/PNG renderer is `Formatting.Markdown` etc.).
- `public DisplayData(string text, string html)` is **removed** (12 call sites, 7 files).
- Registry is static with a lock; `Format<T>` throws `InvalidOperationException` when no
  chain exists.
- Primitives gets `<LangVersion>latest</LangVersion>` + an `IsExternalInit` shim so records
  compile on netstandard2.0.

## Phases — ALL COMPLETE (branch `feature/display-formatters`)

- [x] **P0** — LangVersion + `IsExternalInit` shim in Primitives (whole package converted to
  file-scoped namespaces: IDE0161 applies once the LangVersion rises, and CI verifies format).
- [x] **P1** — concepts + `DisplayFormatters` + `DisplayValues`/`DisplayCell` + packager.
- [x] **P2** — `ClrKernel.Formatting.Html` package; composition root + all three test-suite
  mirrors register it; slnx + release pack list.
- [x] **P3** — engine: trailing value routes through the packager; trailing
  `DisplayCell`/`DisplayedValue` suppressed.
- [x] **P4** — SQL/DAX/`DataResults` → `DisplayTable`; PowerShell → `DisplayConsoleText`;
  the two-string `DisplayData` ctor deleted.
- [x] **P5** — `DisplayBytes` end-to-end (base64 on the wire; the extension decodes binary
  mimes into byte output items — `src/outputItems.ts`, unit-tested).
- [x] **Endgame** — `ResultFormatter`/`InteractiveTable`/`AnsiRenderer` physically moved to
  `Formatting.Html`; Primitives contains zero HTML.

### Deviations from the plan above (all deliberate)

- **`TableExtractor` lives in Primitives**, not the plugin: shaping a reader/DataTable/
  sequence into `DisplayTable` is concept work, not rendering. `DisplayObject → DisplayTable`
  is therefore a built-in registration and works with no render package loaded.
- **`DisplayBadge` concept added** (label pill + text + optional `Success` tone) for the SQL
  run summary and MERGE outcome — the alternative was leaving pill HTML inside `Language.Sql`.
- **`ProgressBar` stayed in Primitives** but rewritten: it publishes `DisplayProgress`
  through a `DisplayCell`; the bar is drawn by the plugin's `ProgressHtml`.
- **HTTP and Mermaid keep their bespoke renderers** (a response card and a diagram are
  renders by nature); they build their MIME bundles explicitly. If they ever need to be
  pluggable, give each a concept + a formatter registered by its own package.
- **`DisplayData(string text)` now sets `text/plain` only** (it used to duplicate unescaped
  text into `text/html`).
- Old `Display(mimeType)` extension replaced by `DisplayValues.Display`; `DisplayAs`
  and `DisplayedValue` remain for raw-MIME updates. `DisplayTable()` overloads return
  `DisplayCell` now.

Each phase is an individual commit on the branch; the suite (`./build.sh Test`) and
`dotnet format --verify-no-changes` are green at every commit from P1 on.

## The cleanup round (review notes, all applied)

- **One display channel.** `DisplayCell.Update` only raises
  `DisplayValues.OnCellDisplayed/OnCellUpdated`. No captured delegates anywhere: each host
  listens once, bundles via `MimeBundler`, and routes by `DisplayId` (its own map of
  display-id → parent request / notebook cell), which is how background updates keep
  reaching the originating output. `DisplayDataEmitter` is gone.
- **`DisplayData` and the bundler live in `Core.Scripting`** (`MimeBundler`, was
  `DisplayDataPackager`) — it is the wire bundle transports serialize, not a concept.
  Anything below `Core.Scripting` (`Database*`) physically cannot construct one.
- **`DisplayedValue` and `DisplayExtensions` are deleted**; `DisplayCell` +
  `DisplayValues` are the whole handle API. `TableExtractor` moved to
  `Formatting.Html` and is registered by the package.
- **`DataResults` and the HTTP response card are self-registering concepts** — a
  package that owns a bespoke shape registers its own concept→concept /
  concept→render formatters in its static ctor. `SsasConnection` returns
  `DisplayTable`/`DisplayText`; the engine bundles any `IDisplayValue` a language returns.
- **`ProgressBar` folded into `DisplayProgress`**: one stateful concept with
  `Show`/`Report`/`SetTotal`/`Done` that re-displays itself through its cell.
- **`connections.local.json`** (and `clrkernel.connections.local.json`) overlays the
  shared file: nearest directory with any candidate wins; base loads first, local
  overrides by name. Personal dev servers stay out of git.

## Dependency layering, as built

The successor to HANDOFF-17 §4.2, derived from the actual `ProjectReference` entries
(23 projects, four tiers). Arrows point at dependencies; a project sits above
everything it references.

```
LEAF (reference nothing)
  Core.Primitives ── display concepts + DisplayFormatters registry (NO HTML)
  Core.Secrets                     Database.Entra

  Core.Scripting ────► Core.Primitives        (engine + language contracts)
  Formatting.Html ───► Core.Primitives        (every default render; referenced ONLY by the CLI)
  DataEngineering ───► Core.Primitives
  Database ──────────► Core.Secrets, Core.Primitives

  Core.LanguageServices ─► Core.Scripting

DATA PROVIDERS
  Provider.SqlServer ────────► Database, DataEngineering, Core.Secrets, Core.Primitives
  Provider.AnalysisServices ─► Database, Database.Entra, Core.Primitives
  Provider.Fabric ───────────► Database.Entra, Core.Primitives      (no Database ref)
  Provider.{Oracle, Odbc, Jdbc} ─► Database                         (opt-in via #r nuget:)

CELL LANGUAGES (contracts from Core.Scripting; emit concepts, never render)
  Language.Sql ──► Core.Scripting, Provider.SqlServer, Database, DataEngineering, Core.Primitives
  Language.Dax ──► Core.Scripting, Provider.AnalysisServices, Database, Core.Primitives
  Language.{Http, Mermaid, PowerShell} ──► Core.Scripting, Core.Primitives

HOSTS
  Core.{JupyterKernel, ExtensionServer} ─► Core.Scripting, Core.LanguageServices, Core.Primitives
  Core.Runner ───────────────────────────► Core.Scripting, Core.Primitives

COMPOSITION ROOT
  ClrKernel (CLI) ─► the three hosts + Formatting.Html
                     + Language.{Http, Mermaid, PowerShell, Sql, Dax} + Provider.Fabric
                     Program.Main registers every cell language AND every default render.
```

The rules that keep the shape honest (enforce by reading csproj, not by intent):

- `Core.Scripting` references **zero** `Language.*` / `Database.*` / `Formatting.*` projects —
  languages arrive through `CellLanguageRegistry`, renders through `DisplayFormatters`.
- `Core.Primitives` references **nothing** and contains **no HTML** — concepts and the
  registry only.
- `Formatting.Html` is referenced **only by the CLI** (and the test suites' mirrors).
  Languages and providers must not reference it: they emit `IDisplayValue` concepts and
  stay renderer-agnostic. If a new package needs a bespoke visual, it ships a concept and
  registers its own formatter — it never reaches into `Formatting.Html`.
- The language/provider split is load-bearing: `Language.Sql` owns the cell session,
  `Provider.SqlServer` owns connections/bulk-copy/MERGE; same shape for `Language.Dax` vs
  `Provider.AnalysisServices`. `Provider.Fabric` deliberately skips `Database` — it is a
  warehouse writer over Entra, not a `DataSource`.
