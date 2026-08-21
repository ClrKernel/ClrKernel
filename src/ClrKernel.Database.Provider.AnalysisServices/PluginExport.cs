using ClrKernel.Core.Primitives;

// Self-describing plugin export: #r-ing this package into a session registers
// the connection provider's descriptor (a no-op when already built in).
[assembly: ConnectionProviderExport(typeof(ClrKernel.Database.Provider.AnalysisServices.SsasConnectionProvider))]
