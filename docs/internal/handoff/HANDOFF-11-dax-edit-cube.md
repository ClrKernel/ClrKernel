# Edit cube for `#!dax` cells (parity with SQL Edit connection)

Adds an **"Edit cube…"** option to the DAX cube dropdown, mirroring the SQL
"Edit connection" button. All changes are in your repo **uncommitted**; no version
bump (held per your call). Verified: `tsc -p ./` clean, `package.json` valid, server
builds, 13 DAX unit tests pass, `dotnet format` clean.

## What you get

The cube dropdown (the `$(server-environment)` button on `#!dax` cells) now shows
**"$(edit) Edit cube…"** below "Add cube…" when at least one cube exists. Picking it
lets you choose a cube and re-runs the cube wizard **pre-filled** with its current
type (on-prem SSAS / Fabric / Azure AS), server (or Fabric workspace), and model. The
name stays fixed and re-registering overwrites the cube in place. Cube connections are
passwordless (Windows Integrated / Entra), so there's no secret to handle. Also in the
palette as **ClrKernel: DAX: Edit Cube…**.

The wizard reconstructs the cube type from the stored connection: a `powerbi://…/myorg/<workspace>`
server → Fabric (workspace parsed out, model = database), an `asazure://…` server →
Azure AS, otherwise on-prem SSAS. If you change the type mid-edit, the pre-filled
field values are dropped (they wouldn't apply to the new type).

## ⚠️ This one needs a .NET rebuild too

To pre-fill, the `clrkernel/dax/listConnections` RPC now returns each cube's `server`,
`database`, and `auth` (it previously returned only name/describe). So unlike the last
extension-only fix, you must rebuild the **server** as well as recompile the extension:

```bash
dotnet build ClrKernel.slnx -c Release      # picks up the LspServer RPC change
cd editors/vscode && npm run compile        # picks up the Edit cube UI
# then F5
```

## Files to stage

- `src/ClrKernel.Server/Lsp/LspServer.cs` — `dax/listConnections` returns `server` / `database` / `auth`.
- `editors/vscode/src/daxConnections.ts` — Edit cube flow (shared add/edit wizard with pre-fill).
- `editors/vscode/package.json` — registers the `clrkernel.dax.editConnection` command.

```bash
git add src/ClrKernel.Server/Lsp/LspServer.cs \
        editors/vscode/src/daxConnections.ts \
        editors/vscode/package.json \
        docs/handoff/HANDOFF-11-dax-edit-cube.md
```

Suggested commit message:

```
feat(dax): edit cube from the cube dropdown

Add an "Edit cube…" option to the #!dax cube picker (parity with SQL "Edit
connection"): pick a cube and re-run the wizard pre-filled with its type, server /
workspace, and model; re-registering overwrites in place. The dax/listConnections RPC
now returns server/database/auth so the UI can pre-fill.
```

## Note

- No version bump (left at 0.6.1).
- Cube connections remain passwordless — the edit wizard collects no secret. For a
  rare SQL-login cube, `#!dax-connect --user … --secret <env-var>` is still the path.
