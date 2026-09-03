using ClrKernel.Core.Scripting;

// Self-describing plugin export: `#r "nuget: ClrKernel.Language.Python"` registers
// the language with the loading session (a no-op when it is built in).
[assembly: CellLanguageExport(typeof(ClrKernel.Language.Python.PythonCellLanguage))]
