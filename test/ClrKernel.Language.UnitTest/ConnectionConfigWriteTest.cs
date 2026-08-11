using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlBulkCreateDirectiveTest {
    [TestMethod]
    public void Create_flag_sets_CreateIfMissing() {
        var d = SqlEtlDirectives.ParseBulk(
            "#!sql-bulk --from src --to dst --query \"select 1\" --table stg.X --create");
        Assert.IsTrue(d.Options.CreateIfMissing);
    }

    [TestMethod]
    public void Create_is_off_by_default() {
        var d = SqlEtlDirectives.ParseBulk("#!sql-bulk --from src --query \"select 1\" --table stg.X");
        Assert.IsFalse(d.Options.CreateIfMissing);
    }

    [TestMethod]
    public void Create_if_missing_alias_works() {
        var d = SqlEtlDirectives.ParseBulk("#!sql-bulk --from src --from-table dbo.X --table stg.X --create-if-missing");
        Assert.IsTrue(d.Options.CreateIfMissing);
    }
}
