# Feature: SQL dialects as distinct cell languages (ClrKernel)

## Summary

Split the single `Sql` cell language into one `ICellLanguage` implementation per
**dialect**, so autocompletion, highlighting, and formatting are dialect-correct
instead of generically-SQL. Initial set:

| Display | Providers it can execute on |
|---|---|
| T-SQL | Microsoft.Data.SqlClient, ODBC, JDBC |
| Oracle SQL | OracleCLRDriver, ODBC, JDBC |
| SQL (Generic) | ODBC, JDBC |

Everything ships from the kernel's language registry, so the Jobs web app and the
VS Code extension pick it up without either of them hardcoding a dialect list.

## The distinction that makes this work

There are two different axes in these notes, and keeping them separate is most of
the design:

- **Dialect** — a property of the **cell**. It lives in the notebook file, goes
  into git, and decides syntax, completions, and formatting.
- **Provider / driver** — a property of the **connection**. It decides how the
  statement is transported.

So the parenthetical lists are not part of the language's identity. They're a
**compatibility declaration**: which providers this dialect can run on. Baking a
provider into the cell's language would mean changing connection changes the
cell's language, which is wrong.

The payoff is a check worth having: when a cell's dialect isn't in the selected
connection's provider list — a T-SQL cell pointed at an Oracle connection — warn
at edit time and fail clearly at run time, rather than sending T-SQL to Oracle and
surfacing a parser error from the driver.

## Language registration

- One implementation per dialect — `TSqlLanguage`, `OracleSqlLanguage`,
  `GenericSqlLanguage` — over a shared `SqlLanguageBase` for the behavior they
  genuinely have in common.
- Keep them registered through the existing `ICellLanguage` mechanism. No new
  parallel registry, and nothing dialect-specific in Jobs or the VS Code
  extension.
- Add the new metadata as **defaulted members** (default interface
  implementations or an optional `ISqlCellLanguage`) so existing languages — C#,
  HTTP, Markdown — don't have to change. Adding a required member to
  `ICellLanguage` would break every implementation for the sake of three.

Metadata each dialect needs to expose:

- **Stable id** — what gets written to the file.
- **Display name** — what the button shows.
- **Supported providers** — open strings, not an enum, so a third party adding a
  Postgres dialect doesn't need a kernel change.
- **Group / category** — so the picker can cluster the SQL dialects together
  rather than scattering them among C# and HTTP.
- **Editor language id** — see the Monaco note below.
- **Aliases** — for backward compatibility.

The whole point of the open-string provider list and the shared base is that
adding PostgreSQL or MySQL later is a new registration and nothing else.

## Persisted ids and migration — decide this before writing code

Existing cells are persisted as `sql`. Two options, and they're not equally safe:

- **Keep `sql` meaning Generic**, add `tsql` and `oraclesql`. No migration, no
  existing notebook changes meaning, and old files stay valid forever.

## Monaco: register distinct editor languages

This is the part that will quietly not work otherwise. **Monaco language ids are
global.** If all three dialects register as `sql`, their completion providers and
tokenizers stack on the same id and every SQL cell offers every dialect's
keywords — exactly the confusion this feature is meant to remove.

Register three distinct Monaco languages (`clr-tsql`, `clr-oraclesql`, `clr-sql`),
each with its own tokenizer and completion provider, and have each `ICellLanguage`
report its editor language id. Both clients then just set the model's language
from that metadata.

Alternative, if three tokenizers is too much duplication: one shared `sql` Monaco
language with a completion provider that reads the owning cell's dialect off the
model. Workable, but the provider now needs a model→cell lookup and the tokenizer
stays dialect-blind. The three-language route is cleaner.

Each dialect supplies its own keyword and built-in function set. Later, the
schema-aware IntelliSense from the Connections work merges connection metadata
with these — dialect keywords from the language, object names from the connection.

## UI

- The cell language button shows the **display name only**: `T-SQL`.
- The dropdown shows the supported providers as secondary text under each option,
  grouped by category. That's where the parentheses belong.
- When the cell has a connection selected and the dialect isn't compatible with
  that connection's provider, mark it in the dropdown and on the cell.

## Formatting

Dialect metadata gives a formatter something concrete to target — worth wiring
RightWaySqlFormatter to the T-SQL dialect specifically rather than to "any SQL
cell", so Oracle and generic cells aren't run through T-SQL formatting rules.

## Acceptance criteria

- [ ] Three SQL dialects appear in the language picker, grouped, with provider
      lists shown in the dropdown and not on the button.
- [ ] Both the Jobs web app and the VS Code extension render the list from kernel
      metadata, with no dialect names in either codebase.
- [ ] Existing notebooks with `sql` cells open unchanged.
- [ ] Each dialect completes its own keywords, and none offers another dialect's.
- [ ] A cell whose dialect isn't supported by its connection's provider is flagged
      at edit time and fails with a clear message at run time.
- [ ] Adding a new dialect requires only a new `ICellLanguage` registration —
      no changes in Jobs, the extension, or the kernel's language plumbing.
- [ ] No existing non-SQL `ICellLanguage` implementation needed modification.