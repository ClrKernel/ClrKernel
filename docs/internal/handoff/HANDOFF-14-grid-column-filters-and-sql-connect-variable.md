# Per-column grid filters + `#!sql-connect` C# variable binding

Two independent features. Both build clean (**0 warnings**), pass `dotnet format
--verify-no-changes`, and the full suite is **248 passed / 8 skipped** (the 8 are the
Docker-SQL-Server integration tests). All changes are **uncommitted** in your repo; no
version bump.

---

## Feature 1 — per-column filters in the results grid

The interactive grid (every `#!sql` result set, every C# `.Results()` / `DisplayTable()`)
now filters **per column**, in addition to the existing global filter and sort. You asked
for *both* styles, so both are there and they all combine (AND):

- **Global filter** (unchanged) — the top box, matches any column.
- **Per-column filter row** — a text box under each header; type to substring-match just
  that column. Focus is preserved across keystrokes (the input row is built once, not
  re-rendered per key).
- **Per-column value picker** — a ▾ funnel on each header opens a dropdown of that
  column's **distinct values** as checkboxes, with a search box and **Select all / Clear**
  links. `(null)` sorts first. Checking a subset filters the column to those values; it
  live-applies as you toggle.
- **Clear** button in the toolbar resets every filter (global + all columns) at once.
- The header funnel highlights (`ck-active`) when that column has any active filter, so
  you can see at a glance which columns are constrained. **Analyze** stats recompute
  against the currently-visible rows.

### Where

Entirely in **`src/ClrKernel.Primitives/InteractiveTable.cs`** (self-contained inline
HTML/CSS/JS, netstandard2.0, no framework). Key structural points:

- `Render()` wraps the table in a `.ck-inner` box (border + `overflow:hidden`) and adds a
  **sibling** `.ck-pop` popover so the dropdown isn't clipped by the scroll container. The
  root is `position:relative` and the popover is absolutely positioned against it.
- `thead` holds two rows: `.ck-h` (labels + sort arrow + funnel button, rebuilt on sort)
  and `.ck-f` (the filter inputs, built **once** to keep focus). Both are sticky
  (`top:0` / `top:26px`).
- Filter state: `colText[]` (per-column substring) and `colSel[]` (per-column value set,
  `null` = all). `distinct[]` is a lazy per-column cache. `passRow()` ANDs global +
  colText + colSel.

### How it was verified

Beyond build/test/format: I rendered a real 25-row grid from the built assembly and drove
it in headless Chromium (Playwright) — global filter, per-column text filter (+ funnel
highlight), the value-picker dropdown (distinct list null-first, Clear-all → 0 rows,
single-value select → correct subset), click-outside close, the Clear button reset, sort,
and the Analyze panel all behaved as expected. The extracted grid JS also passes
`node --check`.

---

## Feature 2 — define a connection in `#!sql-connect` and use it from C#

A `#!sql-connect` connection is now also handed to C# cells as a variable, so you can do:

```
#!sql-connect --name analytics --server sql-warehouse --database reports --auth integrated
```
```csharp
analytics.Query("select top 100 * from dbo.Orders").Results()
```

**Auto-binding rule (what you chose):** when `--name` is a valid C# identifier (and not a
keyword), ClrKernel auto-binds a variable of that exact name to
`Sql.Database("<name>")`. No new flag needed for the common case.

- `--var <name>` (aliases `--variable`, `--as`) — pick the variable name explicitly.
  Needed when the connection name isn't identifier-safe, e.g. `--name my-dw --var dw`.
  An invalid identifier passed to `--var` throws a clear `FormatException`.
- `--no-var` (alias `--no-variable`) — register the connection but bind **no** variable.
- `Sql.Database("<name>")` still resolves any connection by name, as before.

### Where

- **`src/ClrKernel.Sql/SqlDirectives.cs`** — `SqlConnectDirective` gained a `Variable`
  property; `ParseConnect` handles `--var/--variable/--as` and `--no-var/--no-variable`;
  new `ResolveVariable` / `IsValidIdentifier` / `_cSharpKeywords` decide the binding.
- **`src/ClrKernel.Sql/SqlSession.cs`** — `Connect(...)` returns the parsed
  `SqlConnectDirective` (was `SqlConnectionSpec`).
- **`src/ClrKernel.Core/InteractiveScriptEngine.cs`** — the `#!sql-connect` branch
  registers each line and, when `directive.Variable` is set, injects
  `var <name> = Sql.Database("<name>");` into the persistent script state so later
  `#!csharp` cells see it. Confirmation output notes the bound variable. (Refactored the
  script-state init into a shared `EnsureScriptStateAsync()`.)
- **`src/ClrKernel.Server/Lsp/LspServer.cs`** — updated the connect call site to take
  `.Spec` off the returned directive.

### How it was verified

`test/ClrKernel.UnitTest/FluentSqlTest.cs` adds `SqlConnectVariableTest` (5 tests):
auto-var from a valid name; null for non-identifier / keyword names; explicit
`--var` / `--as` / `--no-var`; invalid `--var` throws; and an end-to-end test that runs a
`#!sql-connect --name analytics --var dw ...` cell then a `#!csharp` cell using `dw` and
asserts the value. All 5 pass.

---

## Docs updated

- `README.md` — grid feature line now lists per-column filters + value pickers; the SQL
  section documents `--name` auto-binding / `--var` / `--no-var`.
- `samples/Sql.nb.md` — new "Use a connection from C#" section; grid description updated.
- `samples/SqlQuery.nb.md` — note that `#!sql-connect --name analytics` also binds
  `analytics` directly.

## Files changed (this feature pair)

```
src/ClrKernel.Primitives/InteractiveTable.cs
src/ClrKernel.Sql/SqlDirectives.cs
src/ClrKernel.Sql/SqlSession.cs
src/ClrKernel.Core/InteractiveScriptEngine.cs
src/ClrKernel.Server/Lsp/LspServer.cs
test/ClrKernel.UnitTest/FluentSqlTest.cs
README.md
samples/Sql.nb.md
samples/SqlQuery.nb.md
docs/handoff/HANDOFF-14-grid-column-filters-and-sql-connect-variable.md
```
