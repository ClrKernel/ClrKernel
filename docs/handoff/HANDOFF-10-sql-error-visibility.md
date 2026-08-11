# Fix: visible error messages + SQL connection cert-trust + edit connection

Three extension changes prompted by a `#!sql` cell that returned only a stack trace
(no reason), plus a follow-up to let users edit an existing connection.

All changes are in your repo **uncommitted** (extension only — no .NET changes, no
version bump). Verified: `tsc -p ./` type-checks clean; `package.json` valid JSON.

## 1. The error message was invisible (bug)

The server sends `{ name, message, stack }`, but the VS Code controller set
`error.stack` to the raw **.NET** stack trace. .NET stacks are frames only — they
don't start with the `Name: message` line JS stacks do — and VS Code's notebook
error renderer shows `error.stack`. So the cell showed a bare call stack ending at
`SqlSession.Execute … line 88` with no reason. Line 88 is the `catch (SqlException)`
re-throw, i.e. **SQL Server returned an error** and the real reason was hidden.

Fix (`controller.ts`): prepend `Name: message` to the .NET stack so the message
shows; fall back to letting VS Code render the message when there's no stack.

## 2. The connection button couldn't set certificate trust (likely your error)

`SqlConnectionSpec` / `#!sql-connect` support `--encrypt` / `--trust-cert`, but the
wizard never collected them — so every connection used `Encrypt=true` with
certificate **validation**. A local / on-prem SQL Server (e.g. `badmonkeySQL1`) with
a self-signed certificate then fails with *"the certificate chain was issued by an
authority that is not trusted"* — the `SqlException` caught at line 88.

Fix (`sqlConnections.ts`): new **Encryption** step in the wizard — *Encrypt + trust
the server certificate* (self-signed / local), *Encrypt + validate* (default, Azure
SQL), or *Do not encrypt*. Emits `--trust-cert` / `--encrypt false`.

## 3. Edit connection (new)

The connection dropdown now shows **"$(edit) Edit connection…"** below "Add
connection…" (when at least one connection exists). It lets you pick a connection and
re-runs the wizard **pre-filled** with its current server / database / auth (and
username); the name is fixed, and the **password can be left blank to keep the one
already in your OS credential store**. Re-registering overwrites the connection in
place (the kernel only rewrites the stored secret when a non-empty password is sent,
so blank preserves it). Also available as **ClrKernel: SQL: Edit Connection…** in the
command palette.

## Confirm the original error right now (no recompile needed)

Your existing `badmonkeySQL1` was created without cert trust, so it still fails.
Re-register it with `--trust-cert` in a cell and re-run your query:

```sql
#!sql-connect --name badmonkeySQL1 --server <your-server> --database <your-db> --auth <sql|integrated> --trust-cert --default
```

If that returns rows, the certificate was the issue. After `npm run compile` + F5,
use the new Encryption option (or Edit connection) so you don't have to hand-write it,
and any *other* error will now show its message instead of a bare stack.

## Files to stage

- `editors/vscode/src/controller.ts` — surface the error message.
- `editors/vscode/src/sqlConnections.ts` — Encryption step + Edit connection.
- `editors/vscode/package.json` — registers the `clrkernel.sql.editConnection` command.

(`package-lock.json` is unchanged except it's back at 0.6.1 — no version bump.)

```bash
git add editors/vscode/src/controller.ts editors/vscode/src/sqlConnections.ts \
        editors/vscode/package.json \
        docs/handoff/HANDOFF-10-sql-error-visibility.md
```

Suggested commit message:

```
fix(vscode): surface error messages, add SQL cert-trust + edit-connection

Notebook error output showed a bare .NET stack with no message (VS Code renders
error.stack, and .NET stacks omit the "Name: message" line) — prepend the message so
the reason is visible. Add an Encryption step to the SQL connection wizard
(--trust-cert / --encrypt false) so local / on-prem servers with self-signed certs
can connect. Add an "Edit connection" option to the connection dropdown that re-runs
the wizard pre-filled (blank password keeps the stored secret).
```

## Note

- **Recompile** to pick this up: `cd editors/vscode && npm run compile`, then F5. No server/.NET rebuild needed.
- No version bump — left at 0.6.1 per your call to hold versions until you're ready.
