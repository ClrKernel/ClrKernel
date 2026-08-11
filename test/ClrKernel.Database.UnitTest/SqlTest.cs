using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlSecretTest {
    [TestMethod]
    public void InMemory_provider_round_trips() {
        var p = new InMemorySecretProvider();
        p.Set("sql:analytics", "s3cret");
        Assert.IsTrue(p.TryGet("sql:analytics", out var got));
        Assert.AreEqual("s3cret", got);
        p.Delete("sql:analytics");
        Assert.IsFalse(p.TryGet("sql:analytics", out _));
    }

    [TestMethod]
    public void Environment_provider_maps_key_to_var_name() {
        Assert.AreEqual("CLRKERNEL_SECRET_SQL_ANALYTICS", EnvironmentSecretProvider.EnvName("sql:analytics"));
    }

    [TestMethod]
    public void Store_resolves_from_memory_and_throws_when_missing() {
        var store = SecretStore.ForProviders(new InMemorySecretProvider());
        store.Store("sql:dw", "pw");
        Assert.AreEqual("pw", store.Resolve("sql:dw"));

        var threw = false;
        try {
            store.Resolve("sql:missing");
        } catch (SecretNotFoundException) {
            threw = true;
        }
        Assert.IsTrue(threw, "missing secret should throw SecretNotFoundException");
    }
}

[TestClass]
public class SqlConnectionSpecTest {
    [TestMethod]
    public void SqlPassword_injects_user_and_resolved_secret_only() {
        var mem = new InMemorySecretProvider();
        var store = SecretStore.ForProviders(mem);
        var spec = new SqlConnectionSpec {
            Name = "analytics",
            Server = "pg",
            Database = "reports",
            Auth = SqlAuthMode.SqlPassword,
            User = "sa",
        };
        mem.Set(spec.EffectiveSecretRef, "p@ss");

        var cs = spec.BuildConnectionString(store);
        var parsed = new SqlConnectionStringBuilder(cs);
        Assert.AreEqual("pg", parsed.DataSource);
        Assert.AreEqual("reports", parsed.InitialCatalog);
        Assert.AreEqual("sa", parsed.UserID);
        Assert.AreEqual("p@ss", parsed.Password);
    }

    [TestMethod]
    public void Default_secret_ref_is_derived_from_name() {
        var spec = new SqlConnectionSpec { Name = "warehouse", Auth = SqlAuthMode.SqlPassword };
        Assert.AreEqual("sql:warehouse", spec.EffectiveSecretRef);
        Assert.IsTrue(spec.NeedsSecret);
    }

    [TestMethod]
    public void Missing_password_secret_surfaces_a_clear_error() {
        var store = SecretStore.ForProviders(new InMemorySecretProvider());
        var spec = new SqlConnectionSpec { Name = "x", Server = "s", Auth = SqlAuthMode.SqlPassword, User = "u" };
        var threw = false;
        try {
            spec.BuildConnectionString(store);
        } catch (SecretNotFoundException) {
            threw = true;
        }
        Assert.IsTrue(threw);
    }
}
