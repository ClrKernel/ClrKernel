# ClrKernel Notebooks (VS Code)

Run C# notebooks in VS Code on [ClrKernel](https://github.com/ClrKernel/ClrKernel)
— no Python, no Jupyter. Files matching `*.nb.md` open as notebooks: fenced
`csharp` blocks are code cells, everything else is markdown. The same files are
readable markdown on GitHub and runnable headlessly via ClrKernel's
`#!import`.

## How it works

The extension spawns `ClrKernel.Server` (JSON-RPC over stdio) and executes
cells through the same `ClrKernel.Core` engine the Jupyter kernel uses:
REPL state across cells, `#r "nuget: ..."`, `#!import`/`#!lib` with
prefixes, and live-updating displays.

## Setup (development)

```bash
# build the server
dotnet build src/ClrKernel.Server/ClrKernel.Server.csproj -c Release

# build the extension
cd editors/vscode
npm install
npm run compile
```

Point the extension at the server in VS Code settings:

```json
{
  "clrkernel.server.command": "dotnet",
  "clrkernel.server.args": ["<repo>/src/ClrKernel.Server/bin/Release/net8.0/ClrKernel.Server.dll"]
}
```

(Once `ClrKernel.Server` is published as a dotnet tool, the default
`clrkernel-server` command works with no configuration.)

Launch with F5 (Extension Development Host), open `samples/hello.nb.md`, and
run cells with the **ClrKernel C#** controller.
