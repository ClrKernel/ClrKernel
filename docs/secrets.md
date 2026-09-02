# Passwords and secrets

**A password is never written into a notebook, a `connections.json`, or anything else
that gets committed.** What is stored is a *secret reference* — a name — and the value
behind it is looked up when a cell runs. That rule holds for every connection type, every
notification channel and every git remote in ClrKernel, and it is why a notebook is safe
to push.

```
#!sql-connect --name warehouse --server db.internal --database sales --secret warehouse-pw
                                                                     ^^^^^^^^^^^^^^^^^^^^
                                                                     a name, not a password
```

A reference is any string you like — `warehouse-pw`, `pg:demo`, `NIGHTLY_TOKEN`. Pick one
per credential and use it wherever that credential is needed.

## Where a value is looked up

The first of these that has the name wins:

| | Where | Good for |
| --- | --- | --- |
| 1 | An in-memory cache for this session | Repeat lookups; nothing is stored |
| 2 | **The OS credential store** — macOS Keychain, Windows Credential Manager, Linux libsecret | A laptop. This is the one you want |
| 3 | **A JSON file**, when `CLRKERNEL_SECRETS_FILE` names one | A machine with no credential store at all |
| 4 | **An environment variable**, `CLRKERNEL_SECRET_<REF>` | CI, containers, anything that already manages the secret |

A value that is written — by Studio's connection editor, or by the connection button in
VS Code — goes to the first of those that can keep one, which on a laptop is the OS
store. The environment is read-only by nature, so nothing is ever written there.

Nothing is consulted that is not in this list, and a missing name is an error naming the
variable it looked for rather than a driver-level failure with no reference in it.

## The name in each store

The reference is spelled differently in each, because the stores disagree about what a
name may contain:

| Store | How the reference `pg:demo` is spelled |
| --- | --- |
| Environment | `CLRKERNEL_SECRET_PG_DEMO` — upper-cased, every non-alphanumeric character folded to `_` |
| macOS Keychain | a generic password, service `ClrKernel`, account `pg:demo` — verbatim |
| Windows Credential Manager | a generic credential, target `ClrKernel:pg:demo` |
| Linux libsecret | attributes `service=ClrKernel`, `account=pg:demo` |
| `CLRKERNEL_SECRETS_FILE` | a JSON key, `"pg:demo"` — verbatim |

Only the environment form rewrites the name, and it is the one that has to: a variable
cannot contain a colon.

## Setting one by hand

Studio's connection editor and the VS Code connection button both save a password for
you, and neither needs any of this. These are for a server, a CI job, or a credential
that arrives from somewhere else.

**macOS**

```bash
security add-generic-password -a "warehouse-pw" -s ClrKernel -w 'the password' -U
```

`-U` replaces an existing item rather than failing. To check or remove it:

```bash
security find-generic-password -a "warehouse-pw" -s ClrKernel -w
security delete-generic-password -a "warehouse-pw" -s ClrKernel
```

**Windows**

Save it from Studio's connection editor or the VS Code connection button, which write a
generic credential under the target name in the table above. For automation, use the
environment variable below — it needs nothing installed and behaves the same everywhere.

Credential Manager stores the value as a UTF-16 blob, so a credential written by another
tool is only interchangeable if it does the same; the item is visible under
**Credential Manager → Windows Credentials → `ClrKernel:<ref>`** either way.

**Linux**

```bash
secret-tool store --label "ClrKernel warehouse-pw" service ClrKernel account warehouse-pw
# reads the password from stdin, so it never appears in your shell history
```

**Anywhere, for one run**

```bash
CLRKERNEL_SECRET_WAREHOUSE_PW='the password' clrkernel run reports/monthly.nb.md
```

## The secrets file

`CLRKERNEL_SECRETS_FILE` points at a flat JSON object — references to values:

```json
{
  "warehouse-pw": "the password",
  "pg:demo": "another one"
}
```

It exists for a machine with no credential store, which in practice means a container.
It is **unencrypted** — as protected as the disk it sits on — so it is created
owner-only, and it must not live in a git worktree where a push would take it with it.
Nothing sets the variable for you.

Prefer a real store where there is one. Studio's container image can run a keyring on its
data volume instead; see [docker.md](docker.md#passwords).

## Which secret a thing uses

| What | Where the reference is written |
| --- | --- |
| A SQL connection in a notebook | `#!sql-connect --secret <ref>` |
| A connection in `connections.json` | `"password": { "secret": "<ref>" }` |
| A connection saved in Studio | the **Secret reference** field on the Connections page |
| A notification channel | the channel's token field, on the Channels page |
| A git remote | `remoteSecret` on the project — see [studio.md](studio.md#git-remotes) |

For where Studio puts a password it saves for you, and what it does when it has nowhere
to put one, see [studio.md](studio.md#where-a-saved-password-goes).

## Embedding ClrKernel under another name

`ClrKernel` — the keychain service, the `CLRKERNEL_` variable prefix — comes from one
setting. A product that embeds ClrKernel as a library passes its own prefix to
`SecretStore` and gets `ACME_SECRET_*` variables and an `Acme` keychain service instead,
without forking anything:

```csharp
var secrets = new SecretStore("Acme");
secrets.EnvName("warehouse-pw");   // "ACME_SECRET_WAREHOUSE_PW"
```

`SecretStore.EnvName` is the one place to build such a name — messages that tell somebody
to set a variable should ask the store rather than concatenating a prefix, or they will
name the wrong variable for everyone who is not using the default.

This changes nothing for the `clrkernel` tool or for Studio: both use the default, which
is `ClrKernel`, and every name in this document is the default's.
