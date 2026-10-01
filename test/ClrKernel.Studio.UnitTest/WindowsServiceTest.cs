using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

[TestClass]
public class WindowsServiceTest {
    [TestMethod]
    public void A_global_tool_registers_dotnet_plus_its_dll_with_paths_made_absolute() {
        var flags = new Dictionary<string, string> {
            ["notebooks"] = "notebooks",
            ["data-dir"] = "data",
            ["store"] = "sqlserver",
            ["connection-string"] = "Server=.;Database=x;Integrated Security=true",
            ["service-name"] = "Custom",
            ["account"] = @"DOMAIN\svc",
            ["password"] = "hunter2",
        };
        var binPath = WindowsService.BinPath(@"C:\Program Files\dotnet\dotnet.exe", @"C:\tools\.store\a\ClrKernel.Studio.dll", flags);

        StringAssert.StartsWith(binPath, "\"C:\\Program Files\\dotnet\\dotnet.exe\" C:\\tools\\.store\\a\\ClrKernel.Studio.dll serve ");
        StringAssert.Contains(binPath, "--notebooks " + Path.GetFullPath("notebooks"));
        StringAssert.Contains(binPath, "--data-dir " + Path.GetFullPath("data"));
        StringAssert.Contains(binPath, "--connection-string \"Server=.;Database=x;Integrated Security=true\"", "a value with spaces is quoted");
        Assert.IsFalse(binPath.Contains("hunter2") || binPath.Contains("svc") || binPath.Contains("Custom"),
            "the service's own flags are not serve flags");
    }

    [TestMethod]
    public void An_apphost_is_registered_on_its_own() {
        var binPath = WindowsService.BinPath(@"C:\apps\ClrKernel.Studio.exe", @"C:\apps\ClrKernel.Studio.dll",
            new Dictionary<string, string> { ["notebooks"] = @"C:\nb", ["data-dir"] = @"C:\data" });
        StringAssert.StartsWith(binPath, @"C:\apps\ClrKernel.Studio.exe serve ");
        Assert.IsFalse(binPath.Contains(".dll"));
    }
}
