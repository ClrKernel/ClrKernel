using ClrKernel.Core.Primitives;

// The opt-in flow: #r "nuget: ClrKernel.Database.Provider.Odbc" registers this
// descriptor with the loading session, so connection UIs can describe ODBC.
[assembly: ConnectionProviderExport(typeof(ClrKernel.Database.Provider.Odbc.OdbcConnectionProvider))]
