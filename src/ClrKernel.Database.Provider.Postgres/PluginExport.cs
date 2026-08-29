using ClrKernel.Core.Primitives;

// The opt-in flow: #r "nuget: ClrKernel.Database.Provider.Postgres" registers this
// descriptor with the loading session, so connection UIs can describe PostgreSQL.
[assembly: ConnectionProviderExport(typeof(ClrKernel.Database.Provider.Postgres.PostgresConnectionProvider))]
