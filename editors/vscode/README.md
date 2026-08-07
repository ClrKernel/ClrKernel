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
- **IntelliSense that knows your session** — completion, hover, and signature
  help from a built-in language server (no C# Dev Kit needed). Completions
  reflect the live session: variables from cells you've run, types from
  `#r "nuget:"` packages, and your imports.
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
2. Install this extension.
3. Run a cell. The first time, if `ClrKernel` isn't found the extension
   offers to install it for you (`dotnet tool install --global ClrKernel`).
   Prefer to do it yourself? Run that command in a terminal ahead of time.
4. Create a notebook — either run **ClrKernel: New Markdown Notebook** from the
   Command Palette (or File → New File… → *Markdown Notebook*), or make a file
   ending in `.nb.md`:

   ````markdown
   # My first ClrKernel notebook

   ```csharp
   Console.WriteLine("Hello from ClrKernel");
   ```
   ````

5. Run the cell with the **ClrKernel C#** controller.

`.nb.md` files open as notebooks automatically. If one opens as plain text
instead (a pre-existing editor association can win), right-click it →
**Reopen Editor With…** → **ClrKernel Markdown Notebook**, or add
`"workbench.editorAssociations": { "*.nb.md": "clrkernel-markdown" }` to your
settings.

## Settings

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `clrkernel.server.command` | `clrkernel` | Command that launches the server. The default works when the `ClrKernel` global dotnet tool is installed. |
| `clrkernel.server.args` | `["lsp"]` | Arguments for the command. For a dev build, set command to `dotnet` and args to `["<path>/ClrKernel.dll", "lsp"]`. |

The server's log (including anything it writes to stderr) is in the
**ClrKernel** output channel (View → Output).

## How it works

The extension spawns `clrkernel lsp` — a Language Server over stdio — and talks
to it with a single connection that carries both cell execution and the language
features (completion, hover, signature help). Because execution and IntelliSense
share one process and one `ClrKernel.Core` engine, completions reflect exactly
what you've run. One server runs per VS Code window; REPL state is shared across
notebooks in that window, like a Jupyter kernel session.

## Requirements

- .NET runtime 8.0+ (newer majors work)
- `ClrKernel` on PATH (dotnet tool) or configured via settings

## Developing this extension

```bash
dotnet build src/ClrKernel/ClrKernel.csproj -c Release
cd editors/vscode
npm install
npm run compile
```

Open `editors/vscode` in VS Code and press F5 — the Extension Development Host
launches with `samples/` open. Point the settings at your built `ClrKernel.dll`
(command `dotnet`, args `[<path-to-dll>, "lsp"]`).

## License

[Apache-2.0](https://github.com/ClrKernel/ClrKernel/blob/main/LICENSE)
