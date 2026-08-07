# ClrKernel Notebooks

Run **C# notebooks in VS Code** on [ClrKernel](https://github.com/ClrKernel/ClrKernel) —
no Python, no Jupyter.

Files matching `*.nb.md` open as notebooks: fenced ` ```csharp ` blocks are code
cells, everything else is markdown. The same file is a readable document on
GitHub and runs headlessly in any ClrKernel session via `#!import` — one file,
three lives.

## Features

- **Real C# with REPL state** — variables, classes, and usings persist across
  cells, powered by Roslyn scripting (the same engine as the ClrKernel Jupyter
  kernel).
- **NuGet packages in cells** — `#r "nuget: PackageName, Version"`, plus custom
  feeds via `#i "nuget:<feed-url>"`.
- **Shared libraries** — `#!import "lib.dib"` / `#!lib` with `--register`
  prefixes and run-once semantics; imports `.dib`, `.ipynb`, `.md`, and `.csx`
  files.
- **Live output** — `Console.WriteLine` streams as it happens; displays created
  with `DisplayAs` update in place (progress, timers, tables).
- **Executable markdown** — notebooks that render as plain markdown everywhere
  else and diff cleanly in pull requests.

## Quick start

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or later).
2. Install the ClrKernel server:

   ```bash
   dotnet tool install --global ClrKernel.Server
   ```

3. Install this extension.
4. Create a file called `hello.nb.md`:

   ````markdown
   # My first ClrKernel notebook

   ```csharp
   Console.WriteLine("Hello from ClrKernel");
   ```
   ````

5. Open it — it appears as a notebook. Run the cell with the **ClrKernel C#**
   controller.

## Settings

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `clrkernel.server.command` | `clrkernel-server` | Command that launches the server. The default works when ClrKernel.Server is installed as a global dotnet tool. |
| `clrkernel.server.args` | `[]` | Arguments for the command — e.g. set command to `dotnet` and args to the path of a locally built `ClrKernel.Server.dll`. |

The server's log (including anything it writes to stderr) is in the
**ClrKernel** output channel (View → Output).

## How it works

The extension spawns `ClrKernel.Server` — a small JSON-RPC-over-stdio host —
and executes cells through `ClrKernel.Core`, the same execution engine behind
the [ClrKernel Jupyter kernel](https://www.nuget.org/packages/ClrKernel). One
server runs per VS Code window; REPL state is shared across notebooks in that
window, like a Jupyter kernel session.

## Requirements

- .NET runtime 8.0+ (newer majors work)
- `ClrKernel.Server` on PATH (dotnet tool) or configured via settings

## Developing this extension

```bash
dotnet build src/ClrKernel.Server/ClrKernel.Server.csproj -c Release
cd editors/vscode
npm install
npm run compile
```

Open `editors/vscode` in VS Code and press F5 — the Extension Development Host
launches with `samples/` open. Point the settings at your built
`ClrKernel.Server.dll` (command `dotnet`, args `[<path-to-dll>]`).

## License

[Apache-2.0](https://github.com/ClrKernel/ClrKernel/blob/main/LICENSE)
