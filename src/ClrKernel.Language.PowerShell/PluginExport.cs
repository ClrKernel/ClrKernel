using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

// Self-describing plugin exports: #r-ing this package into a session registers
// the language and its connection provider (no-ops when already built in).
[assembly: CellLanguageExport(typeof(ClrKernel.Language.PowerShell.PowerShellCellLanguage))]
[assembly: ConnectionProviderExport(typeof(ClrKernel.Language.PowerShell.PwshConnectionProvider))]
