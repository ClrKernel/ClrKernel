using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

// Self-describing plugin exports: #r-ing this package into a session registers
// the language and the shared Ssh connection provider (no-ops when built in).
[assembly: CellLanguageExport(typeof(ClrKernel.Language.Shell.ShellCellLanguage))]
[assembly: ConnectionProviderExport(typeof(ClrKernel.Language.Shell.SshConnectionProvider))]
