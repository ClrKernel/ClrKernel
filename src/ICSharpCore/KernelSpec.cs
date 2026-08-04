using System;
using System.IO;

namespace ICSharpCore
{
    public class KernelSpec
    {
        public static FileInfo KernelSpecFile => new FileInfo(Path.Combine(AppContext.BaseDirectory, "..", "..", "any", "kernel-spec", "kernel.json"));

        public static void PrintKernelSpecDetails()
        {
            var kernelSpecFile = KernelSpecFile;
            var content = kernelSpecFile.Exists ? File.ReadAllText(kernelSpecFile.FullName) : "N/A";
            Console.WriteLine(
                $"""
                ICSharpCore Kernel spec details:
                - Path:       {kernelSpecFile.FullName}
                - Directory:  {kernelSpecFile.DirectoryName}
                - Exists:     {kernelSpecFile.Exists}
                - Content:    {content}
                """);
        }

        public static bool HandleKernelSpecRequest(string[] args)
        {
            switch(args)
            {
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
}