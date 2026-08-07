using System;
using System.IO;

namespace ClrKernel.Jupyter;

public class KernelSpec {
    /// <summary>
    /// Locates kernel.json in both supported layouts:
    /// - packed .NET tool:  .store/.../tools/{tfm}/any/ClrKernel.dll  -> spec at tools/any/kernel-spec/
    /// - local build:       bin/{Config}/{tfm}/ClrKernel.dll         -> spec copied alongside at ./kernel-spec/
    /// Falls back to the packed-tool path (non-existent FileInfo) when neither is found.
    /// </summary>
    public static FileInfo KernelSpecFile {
        get {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "any", "kernel-spec", "kernel.json"),
                Path.Combine(AppContext.BaseDirectory, "kernel-spec", "kernel.json")
            };

            foreach (var candidate in candidates) {
                var file = new FileInfo(candidate);
                if (file.Exists) {
                    return file;
                }
            }

            return new FileInfo(candidates[0]);
        }
    }

    public static void PrintKernelSpecDetails() {
        var kernelSpecFile = KernelSpecFile;
        var content = kernelSpecFile.Exists ? File.ReadAllText(kernelSpecFile.FullName) : "N/A";
        Console.WriteLine(
            $"""
            ClrKernel Kernel spec details:
            - Path:       {kernelSpecFile.FullName}
            - Directory:  {kernelSpecFile.DirectoryName}
            - Exists:     {kernelSpecFile.Exists}
            - Content:    {content}
            """);
    }

    public static bool HandleKernelSpecRequest(string[] args) {
        switch (args) {
            case ["--kernel-spec-path"]:
                Console.WriteLine(KernelSpecFile.DirectoryName);
                return true;
            case ["--kernel-spec-details"]:
                PrintKernelSpecDetails();
                return true;
            default:
                return false;
        }
    }
}
