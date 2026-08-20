using ClrKernel.Core.Primitives;

// The opt-in flow: #r "nuget: ClrKernel.Database.Provider.Jdbc" registers this
// descriptor with the loading session, so connection UIs can describe JDBC.
[assembly: ConnectionProviderExport(typeof(ClrKernel.Database.Provider.Jdbc.JdbcConnectionProvider))]
