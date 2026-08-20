using ClrKernel.Core.Primitives;

// The opt-in flow: #r "nuget: ClrKernel.Database.Provider.Oracle" registers this
// descriptor with the loading session, so connection UIs can describe Oracle.
[assembly: ConnectionProviderExport(typeof(ClrKernel.Database.Provider.Oracle.OracleConnectionProvider))]
