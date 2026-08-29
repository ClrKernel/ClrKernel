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

**Two fields, because an editor needs two different things.** The spec has one
"editor language id" and it turned out to be doing two jobs:

- `EditorLanguageId` is an **identity** — the id an editor gives the cell, and
  therefore the id the document comes back under when the editor syncs it.
  Distinct per language, or one would route to the other. `CellLanguageSet.ById`
  resolves it as well as the kernel's own id, which is what makes the round trip
  work.
- `GrammarId` is an **appearance** — the syntax to highlight with when the editor
  has no grammar of its own. Shared by all three dialects, because a tokenizer
  reads strings, comments, numbers and identifiers rather than words, and words
  are all the dialects differ by.

Conflating them shipped first and was wrong in a way that showed immediately: the
dialects all reported `sql`, so VS Code gave T-SQL cells the built-in `sql` id and
the cell picker read "SQL", "Oracle SQL", "SQL (Generic)" — two entries reading
"SQL", no "T-SQL", and the same cell called "T-SQL" in the web app. The dialects
now take `clr-sql`, `clr-oraclesql` and `clr-ansisql`, declared in the VSIX with
the names the kernel gives them and grammars that delegate to `source.sql`.

That fixes a second thing that was never intentional: a cell called `sql` is a
cell every SQL extension the user has installed attaches to, adding its own
completions and squiggles. Exactly what `csharp-script` exists to prevent, and
now prevented the same way.

On Monaco specifically, the spec preferred three registered editor languages, and
named the reason: Monaco's ids are global, so three dialects registering as `sql`
would stack three completion providers on one id. Sound in general, and it does
not apply here — this codebase's providers already consult a model→cell map
(`monaco/language.ts`, `sqlSchema.ts`) and ask the kernel per cell, so completion
is dialect-correct with one shared id. `GrammarId` is what carries that, and a
future dialect that genuinely wants its own tokenizer asks for one by naming it.

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

The cell's languageId **is** `editorLanguageId`, and everything that starts from
`cell.document.languageId` goes through `languageForEditorLanguage`, which also
matches the kernel's own id — a notebook opened before this changed still has
cells called `sql` and they keep working.

## Not done

- **Formatting.** The spec suggests wiring `RightWaySqlFormatter` to T-SQL
  specifically. There is no SQL formatter in this repo at all, so the dialect
  metadata is the groundwork and nothing more.
- **An Oracle parser.** Only T-SQL has one (ScriptDom), so only T-SQL gets syntax
  errors. Running the T-SQL parser over Oracle would reject valid statements with
  confident-sounding messages, which is worse than not checking.
- **A live JDBC run.** The provider is known good on Windows; `Jdbc.FromConfig`,
  which is what makes a JDBC connection nameable from a cell, is new and has
  never been run — IKVM is Windows x64. It is item 14 on the Windows checklist.

**Oracle is done.** Verified end to end against Oracle Free 23ai in Docker
(`gvenzl/oracle-free:slim`, native arm64, now in `dev/docker-compose.dbs.yml`):
six live tests in `OracleDialectLiveTest`, and a browser check running an
`#!oraclesql` cell from the web editor.

The browser check earned its keep immediately. The unit tests passed while the
notebook did not, and could not have caught why: a test project *references* the
Oracle provider, so its assembly is loaded before anything asks — the shipped
kernel does not, and a notebook has to `#r "nuget: …"` first. The refusal that
says so was already right; nothing had ever seen it in the one place it matters.
