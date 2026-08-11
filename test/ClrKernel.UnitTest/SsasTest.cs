using System.Data;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.AnalysisServices;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SsasCSharpCellTest {
    [TestMethod]
    public async Task Ssas_is_usable_from_a_csharp_cell() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        // Ssas is imported into C# cells; Connect builds a spec without touching a server.
        var result = await engine.ExecuteAsync("Ssas.Connect(\"DataWarehouseServer01.yourdomain.local\", \"AdventureWorksDW2025\").Spec.Describe()");
        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "a C# cell should return display data");
        StringAssert.Contains((string)dd.Data["text/plain"], "DataWarehouseServer01.yourdomain.local/AdventureWorksDW2025");
    }
}

[TestClass]
public class SsasConnectionStringTest {
    [TestMethod]
    public void Integrated_builds_adomd_and_tom_strings() {
        var spec = new SsasConnectionSpec { Server = "DataWarehouseServer01.yourdomain.local", Database = "AdventureWorksDW2025" };
        var adomd = spec.BuildAdomdConnectionString();
        StringAssert.Contains(adomd, "Provider=MSOLAP");
        StringAssert.Contains(adomd, "Data Source=DataWarehouseServer01.yourdomain.local");
        StringAssert.Contains(adomd, "Catalog=AdventureWorksDW2025");
        StringAssert.Contains(spec.BuildTomConnectionString(), "Initial Catalog=AdventureWorksDW2025");
    }

    [TestMethod]
    public void User_password_is_included() {
        var spec = new SsasConnectionSpec {
            Server = "s",
            Database = "d",
            Auth = SsasAuthMode.UserPassword,
            User = "svc",
            Password = "p@ss",
        };
        var cs = spec.BuildAdomdConnectionString();
        StringAssert.Contains(cs, "User ID=svc");
        StringAssert.Contains(cs, "Password=p@ss");
    }

    [TestMethod]
    public void Raw_connection_string_is_verbatim() {
        var spec = new SsasConnectionSpec {
            Auth = SsasAuthMode.ConnectionString,
            RawConnectionString = "Data Source=custom;Catalog=x;",
        };
        Assert.AreEqual("Data Source=custom;Catalog=x;", spec.BuildAdomdConnectionString());
        Assert.AreEqual("Data Source=custom;Catalog=x;", spec.BuildTomConnectionString());
    }

    [TestMethod]
    public void AzureAd_leaves_credentials_out_of_the_string() {
        var spec = new SsasConnectionSpec { Server = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "Model", Auth = SsasAuthMode.AzureAd };
        var cs = spec.BuildAdomdConnectionString();
        StringAssert.Contains(cs, "Data Source=powerbi://api.powerbi.com/v1.0/myorg/WS");
        Assert.IsFalse(cs.Contains("Password="), "token is applied to the client, not the string");
    }
}

[TestClass]
public class SsasFactoryTest {
    [TestMethod]
    public void Connect_defaults_to_integrated_and_switches_on_user() {
        Assert.AreEqual(SsasAuthMode.Integrated, Ssas.Connect("s", "d").Spec.Auth);
        Assert.AreEqual(SsasAuthMode.UserPassword, Ssas.Connect("s", "d", "u", "p").Spec.Auth);
    }

    [TestMethod]
    public void ConnectFabric_builds_powerbi_endpoint() {
        var cube = Ssas.ConnectFabric("Analytics WS", "Sales Model");
        Assert.AreEqual(SsasAuthMode.AzureAd, cube.Spec.Auth);
        Assert.AreEqual("powerbi://api.powerbi.com/v1.0/myorg/Analytics WS", cube.Spec.Server);
        Assert.AreEqual("Sales Model", cube.Spec.Database);
        Assert.IsNotNull(cube.Spec.TokenProvider);
    }

    [TestMethod]
    public void FromConnectionString_uses_connection_string_mode() {
        var cube = Ssas.FromConnectionString("Data Source=x;");
        Assert.AreEqual(SsasAuthMode.ConnectionString, cube.Spec.Auth);
    }
}

[TestClass]
public class SsasPartitionDefinitionTest {
    [TestMethod]
    public void Tuple_converts_to_partition_definition() {
        PartitionDefinition byName = ("Sales", "2026");
        Assert.AreEqual("Sales", byName.TableName);
        Assert.AreEqual("2026", byName.PartitionName);

        PartitionDefinition full = ("Sales", "2026", "dwSource", "SELECT * FROM Sales WHERE Yr=2026");
        Assert.AreEqual("dwSource", full.DataSourceName);
        StringAssert.Contains(full.Query, "Yr=2026");
    }
}

[TestClass]
public class SsasDmvMapperTest {
    private sealed class FakeReader : IDataReader {
        private readonly string[] _columns;
        private readonly object[][] _rows;
        private int _index = -1;
        public FakeReader(string[] columns, object[][] rows) { _columns = columns; _rows = rows; }
        public int FieldCount => _columns.Length;
        public string GetName(int i) => _columns[i];
        public object GetValue(int i) => _rows[_index][i];
        public bool Read() => ++_index < _rows.Length;
        // unused members
        public object this[int i] => GetValue(i);
        public object this[string name] => null;
        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => 0;
        public void Close() { }
        public void Dispose() { }
        public bool GetBoolean(int i) => false;
        public byte GetByte(int i) => 0;
        public long GetBytes(int i, long f, byte[] b, int o, int l) => 0;
        public char GetChar(int i) => '\0';
        public long GetChars(int i, long f, char[] b, int o, int l) => 0;
        public IDataReader GetData(int i) => null;
        public string GetDataTypeName(int i) => "";
        public System.DateTime GetDateTime(int i) => default;
        public decimal GetDecimal(int i) => 0;
        public double GetDouble(int i) => 0;
        public System.Type GetFieldType(int i) => typeof(object);
        public float GetFloat(int i) => 0;
        public System.Guid GetGuid(int i) => default;
        public short GetInt16(int i) => 0;
        public int GetInt32(int i) => 0;
        public long GetInt64(int i) => 0;
        public int GetOrdinal(string name) => 0;
        public string GetString(int i) => "";
        public int GetValues(object[] values) => 0;
        public bool IsDBNull(int i) => _rows[_index][i] == null;
        public DataTable GetSchemaTable() => null;
        public bool NextResult() => false;
    }

    [TestMethod]
    public void Maps_columns_by_name_with_name_alias() {
        var reader = new FakeReader(
            new[] { "ID", "Name", "RecordCount" },
            new[] {
                new object[] { 1L, "Sales", 100L },
                new object[] { 2L, "Dates", 50L },
            });
        var rows = DmvMapper.Map<DmvSegmentMapStorage>(reader);
        // DmvSegmentMapStorage has ID + RecordCount (no Name) — verifies unknown
        // columns are skipped and known ones mapped.
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(100L, rows[0].RecordCount);
        Assert.AreEqual(2L, rows[1].ID);
    }
}
