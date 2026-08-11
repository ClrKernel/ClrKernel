# Fix duplicate completions + false "syntax error" squiggles in C# cells

Extension-only change. TypeScript compiles (`tsc`, 0 errors); all JSON validates. **No
kernel change** and **no version bump** (extension stays 0.4.0; this lands in the
`[Unreleased]` changelog). Uncommitted in your repo.

> Can't be runtime-verified in the cloud sandbox — the whole point is how **C# Dev Kit**
> reacts, which needs your Windows dev host. Checklist items added (§4).

## Root cause

Both symptoms are one cause: another C# extension (C# Dev Kit / the Roslyn language
server) was binding to the notebook's `csharp` cells alongside ClrKernel's own language
server.

- **Duplicate completions** — two providers answered completion for `csharp` documents
  (ClrKernel's LSP + C# Dev Kit's). VS Code shows both, so every member appeared twice.
  (Tell: one group included `Display`, a ClrKernel session member C# Dev Kit can't see.)
- **False squiggles** — ClrKernel already parses in `SourceCodeKind.Script` (so a bare
  trailing expression like `x` is valid) and publishes **no** C# diagnostics. The
  squiggles came from C# Dev Kit parsing the cell as a **regular** compilation unit, where
  a trailing expression is a syntax error.

This is the same class of problem .NET Interactive avoided by using a cell language id
that other C# tooling doesn't claim.

## The fix

C# cells now use a dedicated language id **`clrkernel-csharp`** instead of `csharp`, so
C# Dev Kit / Roslyn don't attach — no second completion source, no foreign diagnostics.
Highlighting is preserved by an embedded grammar that delegates to the built-in C# grammar
(`source.cs`), and files still serialize as ` ```csharp `.

The kernel needs no change: its LSP routes SQL/DAX/PowerShell explicitly and treats
**everything else as C#**, so `clrkernel-csharp` documents still get Roslyn completion,
hover, and signature help.

## Files

- `editors/vscode/syntaxes/clrkernel-csharp.tmLanguage.json` (new) — grammar,
  `scopeName: source.clrkernel-cs`, `patterns: [{ include: "source.cs" }]`.
- `editors/vscode/language-configuration.csharp.json` (new) — C#-style brackets, comments,
  auto-closing pairs for the new language.
- `editors/vscode/package.json` — contributes the `clrkernel-csharp` language + grammar.
- `editors/vscode/src/markdownSerializer.ts` — C# fences deserialize to
  `clrkernel-csharp`; serialization still writes ` ```csharp ` (unchanged fallback), so
  `.nb.md` files stay portable.
- `editors/vscode/src/controller.ts` — `supportedLanguages` adds `clrkernel-csharp` (keeps
  `csharp` for any legacy/hand-set cell).
- `editors/vscode/src/serverClient.ts` — LanguageClient `documentSelector` uses
  `clrkernel-csharp` (was `csharp`).
- `editors/vscode/src/extension.ts` — the "new C# cell" command uses `clrkernel-csharp`.

## Verify on Windows (has C# Dev Kit)

1. Completion in a C# cell → each member appears **once**; cell language shows
   **ClrKernel C#**; highlighting still looks like C#.
2. Two cells — `var x = 10;` then a cell containing just `x` — → **no red squiggle**.
3. IntelliSense (completion/hover/signature) and Run still work; open a `.nb.md`, edit a C#
   cell, save → the file still uses ` ```csharp ` fences (round-trip intact).

## If it needs tuning

If C# Dev Kit still squiggles notebook cells regardless of language id, the fallback is to
have ClrKernel publish its own (script-mode) C# diagnostics and/or document turning off the
C# extension's notebook support — but the language-id split is the approach .NET Interactive
used and should be sufficient.

## Files changed

```
editors/vscode/syntaxes/clrkernel-csharp.tmLanguage.json   (new)
editors/vscode/language-configuration.csharp.json          (new)
editors/vscode/package.json
editors/vscode/src/markdownSerializer.ts
editors/vscode/src/controller.ts
editors/vscode/src/serverClient.ts
editors/vscode/src/extension.ts
editors/vscode/CHANGELOG.md
docs/windows-verification-checklist.md
```
