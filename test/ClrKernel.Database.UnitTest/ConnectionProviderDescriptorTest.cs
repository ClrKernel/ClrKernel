using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClrKernel.Core.Primitives;
using ClrKernel.Database.Provider.AnalysisServices;
using ClrKernel.Database.Provider.Odbc;
using ClrKernel.Database.Provider.Oracle;
using ClrKernel.Database.Provider.Postgres;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Database.UnitTest;

/// <summary>
/// Connection-provider descriptors: the drift guard that keeps each schema honest
/// against the config keys its provider actually reads, plus the wire round-trip
/// the RPC surfaces rely on. When a FromConfig/FromNode gains a key, add it to the
/// descriptor AND to the expected set here.
/// </summary>
[TestClass]
public class ConnectionProviderDescriptorTest {
    private static void AssertCovers(ConnectionProviderDescriptor descriptor, params string[] configKeys) {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in descriptor.Settings) {
            known.Add(setting.Name);
            foreach (var alias in setting.Aliases) {
                known.Add(alias);
            }
        }
        foreach (var key in configKeys) {
            Assert.IsTrue(known.Contains(key),
                $"{descriptor.Type}: config key '{key}' is read by the provider but missing from its descriptor");
        }
    }

    [TestMethod]
    public void Sql_server_descriptor_covers_every_config_key_its_reader_uses() =>
        // SqlConnectionConfig.FromNode
        AssertCovers(SqlServerConnectionProvider.Descriptor,
            "server", "host", "database", "user", "username", "auth", "connectionString",
            "encrypt", "trustServerCertificate", "trustCert", "password");

    [TestMethod]
    public void Analysis_services_descriptor_covers_its_reader() =>
        // SsasConnectionConfig.FromNode
        AssertCovers(SsasConnectionProvider.Descriptor,
            "server", "host", "database", "model", "catalog", "user", "username",
            "auth", "connectionString", "password");

    [TestMethod]
    public void Oracle_descriptor_covers_its_reader() =>
        // Oracle.FromConfig
        AssertCovers(OracleConnectionProvider.Descriptor,
            "connectionString", "server", "port", "serviceName", "userId", "user", "password");

    [TestMethod]
    public void Postgres_descriptor_covers_its_reader_and_allows_extras() {
        // PostgresConnectionConfig.ToConnectionString's reserved keys; everything else
        // passes through as an Npgsql keyword, which the descriptor must declare.
        AssertCovers(PostgresConnectionProvider.Descriptor,
            "connectionString", "server", "host", "port", "database", "user", "username",
            "password", "sslMode");
        Assert.IsTrue(PostgresConnectionProvider.Descriptor.AllowExtraSettings);
    }

    [TestMethod]
    public void Odbc_descriptor_covers_its_reader_and_allows_extras() {
        // Odbc.FromConfig reserved keys; everything else passes through, which the
        // descriptor must declare.
        AssertCovers(OdbcConnectionProvider.Descriptor,
            "connectionString", "driver", "dsn", "user", "password");
        Assert.IsTrue(OdbcConnectionProvider.Descriptor.AllowExtraSettings);
    }

    [TestMethod]
    public void Passwords_are_always_secret_references_never_text() {
        foreach (var descriptor in new[] {
            SqlServerConnectionProvider.Descriptor, SsasConnectionProvider.Descriptor,
            OracleConnectionProvider.Descriptor, OdbcConnectionProvider.Descriptor,
            PostgresConnectionProvider.Descriptor,
        }) {
            var password = descriptor.Settings.Single(s => s.Name == "password");
            Assert.AreEqual(ConnectionSettingKind.SecretRef, password.Kind,
                $"{descriptor.Type}: the password setting must be a secret reference");
        }
    }

    [TestMethod]
    public void Credential_values_and_requires_lists_reference_real_things() {
        foreach (var descriptor in new[] {
            SqlServerConnectionProvider.Descriptor, SsasConnectionProvider.Descriptor,
            OracleConnectionProvider.Descriptor, OdbcConnectionProvider.Descriptor,
            PostgresConnectionProvider.Descriptor,
        }) {
            var names = descriptor.Settings.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in descriptor.Settings) {
                foreach (var value in setting.CredentialValues ?? Array.Empty<string>()) {
                    CollectionAssert.Contains(setting.EnumValues.ToList(), value,
                        $"{descriptor.Type}.{setting.Name}: credential value '{value}' is not one of its enum values");
                }
                foreach (var required in setting.Requires ?? Array.Empty<string>()) {
                    Assert.IsTrue(names.Contains(required),
                        $"{descriptor.Type}.{setting.Name}: requires unknown setting '{required}'");
                }
            }
        }
        // The pairs the wizard flows are built on.
        var sqlAuth = SqlServerConnectionProvider.Descriptor.Settings.Single(s => s.Name == "auth");
        CollectionAssert.AreEquivalent(new[] { "sql", "entra-password" }, sqlAuth.CredentialValues.ToList());
        var workspace = SsasConnectionProvider.Descriptor.Settings.Single(s => s.Name == "workspace");
        CollectionAssert.AreEqual(new[] { "model" }, workspace.Requires.ToList());
    }

    [TestMethod]
    public void Runtime_only_settings_carry_no_directive_or_config_shape() {
        var tokenProvider = SsasConnectionProvider.Descriptor.Settings.Single(s => s.Name == "tokenProvider");
        Assert.IsTrue(tokenProvider.RuntimeOnly);
        Assert.IsNull(tokenProvider.DirectiveFlag, "a runtime-only setting has no directive form");
    }

    [TestMethod]
    public void Descriptors_round_trip_the_camel_case_wire_shape() {
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        var json = JsonSerializer.Serialize(SqlServerConnectionProvider.Descriptor, options);
        StringAssert.Contains(json, "\"type\":\"SqlServer\"");
        StringAssert.Contains(json, "\"directiveFlag\":\"--server\"");

        var back = JsonSerializer.Deserialize<ConnectionProviderDescriptor>(json, options);
        Assert.AreEqual("SqlServer", back.Type);
        Assert.AreEqual("#!sql-connect", back.ConnectSelector);
        var auth = back.Settings.Single(s => s.Name == "auth");
        Assert.AreEqual(ConnectionSettingKind.Enum, auth.Kind);
        CollectionAssert.AreEqual(
            new[] { "sql", "integrated", "entra", "entra-password", "entra-interactive" }, auth.EnumValues.ToList());
    }
}
