using ClrKernel.Core.Scripting;

// Self-describing plugin export: #r-ing this package into a session registers
// the language (a no-op when it is already built in).
[assembly: CellLanguageExport(typeof(ClrKernel.Language.Mermaid.MermaidCellLanguage))]
