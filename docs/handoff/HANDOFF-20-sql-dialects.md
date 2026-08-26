# HANDOFF-20 — SQL as dialects, and dialects as a cell property

*Landed 2026-08-25. Spec: `docs/Sql-dialects-spec.md`.*

`#!sql` was one language that meant "T-SQL on Microsoft SQL Server". It is now
one of three dialects — `sql` (T-SQL), `oraclesql`, `ansisql` — and a dialect can
run on more than one provider.

## The distinction the whole thing rests on

- **Dialect** — a property of the **cell**. It is in the file, it goes into git,
  and it decides which words are legal.
- **Provider** — a property of the **connection**. It decides what carries the
  statement.

`ICellLanguage.SupportedProviders` is the join, and it is deliberately a
*compatibility declaration* rather than part of a language's identity. Baking a
provider into the language would mean changing a cell's connection changes what
language the cell is written in, which is wrong. Open strings rather than an
enum, so a third party shipping a PostgreSQL dialect needs no change in the
kernel.

The payoff is a check worth having: a T-SQL cell pointed at an Oracle connection
is a warning while you write it and a refusal naming both halves when you run it,
instead of a parser error from a driver that names neither.

## Two decisions that were not the spec's

**`sql` stays T-SQL.** The spec proposed handing `sql` to the new generic dialect
and giving T-SQL a new `tsql` id, on the grounds that no existing notebook's
bytes change. Its bytes would not; its *meaning* would. `sql` has been T-SQL
since the first release — checked by the T-SQL parser, tagged ```` ```tsql ````,
executed on SQL Server — so every notebook already written would have silently
become a generic-SQL notebook with no T-SQL completion and no syntax check. The
new dialects took new ids instead. Nothing migrates.

**One Monaco language, not three.** The spec preferred three registered editor
languages and named the reason: Monaco's language ids are global, so three
dialects registering as `sql` would stack three completion providers on one id
and every SQL cell would offer every dialect's keywords. That reasoning is sound
in general and does not apply here — this codebase's providers already consult a
model→cell map (`monaco/language.ts`, `sqlSchema.ts`) and ask the kernel per
cell, so completion is dialect-correct with one shared id. The spec's own
"Alternative" is the cheaper correct route *in this codebase*. `EditorLanguageId`
carries it as metadata, so a future dialect that genuinely wants its own
tokenizer can ask for one.

## Where the sharing happens

Three dialects, **one session**. A connection belongs to the notebook, not to the
dialect that declared it: `#!sql-connect --name dw` then `#!ansisql --connections
dw` has to be one connection, or a name would mean a different thing in every
cell depending on how it was written. A shared session must not outlive its
engine, so `CellLanguageRegistry` grew a second constructor taking a factory per
*family* rather than per language — sharing happens inside the factory call.
Registering with the old constructor is unchanged and wraps each language as a
family of one.

`SqlCellLanguage.Current` still points at the T-SQL instance, so the C# `SqlServer`
global is exactly what it was.

## Provider-agnostic execution

`DataSourceCatalog` (in `ClrKernel.Database`) opens a `connections.json` node of
any `$type` by the convention that `ClrKernel.Database.Provider.X` exposes
`X.FromConfig(name, secrets)`. Found by **reflection at the moment of the
question**, because the opt-in providers are `#r`-loaded partway through a
session: a registry they write themselves into needs their code to have run
first, which is exactly what has not happened yet. A `$type` nobody can open
answers with the `#r` line to paste rather than "connection not found".

`Jdbc` gained the `FromConfig` its own descriptor had been calling a follow-up.
It is **untested against a live driver** — IKVM is Windows x64 — and belongs on
`docs/windows-verification-checklist.md`.

**The SQL Server path is untouched.** Bulk copy, MERGE, the deploy planner and
error messages carrying SQL Server message numbers are what every existing
notebook runs through. Non-SQL-Server connections take a separate path over
`DataSource`; the four verbs stay SQL Server's own.

## What each client had to do

**Jobs**: nothing dialect-specific. Grouping comes from `category`, the option
subtitles from `supportedProviders`, the highlighter from `editorLanguageId`. A
test adds a PostgreSQL descriptor and asserts it lands in the right group with
the right subtitle without the web app being edited.

**VS Code**: behaviour needed nothing — fences, selectors, routing and the
connections UI are all descriptor-driven. *Presentation* is static VSIX JSON, so
the two new ids are declared in `package.json` with grammars that delegate to
`source.sql`. That is the one acceptance criterion ("no dialect names in either
codebase") that cannot be met, and it is the same carve-out `csharp-script`,
`http`, `mermaid` and `dax` already live under. A test guards the drift: every
bundled language must be declared in the manifest or be a VS Code built-in.

The cell's languageId stays the kernel's id and is deliberately **not**
`editorLanguageId` — that id is what routes a cell back to its language, so two
dialects sharing one would send Oracle SQL to T-SQL.

## Not done

- **Formatting.** The spec suggests wiring `RightWaySqlFormatter` to T-SQL
  specifically. There is no SQL formatter in this repo at all, so the dialect
  metadata is the groundwork and nothing more.
- **An Oracle parser.** Only T-SQL has one (ScriptDom), so only T-SQL gets syntax
  errors. Running the T-SQL parser over Oracle would reject valid statements with
  confident-sounding messages, which is worse than not checking.
- **A live run against Oracle or JDBC.** The path is tested against fakes and the
  catalog against real provider assemblies; neither has been run against a real
  Oracle instance from this machine.
