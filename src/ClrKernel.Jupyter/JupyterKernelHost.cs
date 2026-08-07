using System;
using System.IO;
using ClrKernel.Core;
using ClrKernel.Jupyter.Protocols;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jupyter;

/// <summary>
/// Entry point for running the Jupyter kernel. Extracted from the former
/// ClrKernel.Program.Main so the kernel can be hosted by the ClrKernel CLI
/// (<c>clrkernel jupyter &lt;connection_file&gt;</c>) or embedded by another host.
/// </summary>
public static class JupyterKernelHost {
    /// <summary>
    /// Runs the kernel event loop. <paramref name="args"/> is the argument list
    /// that follows the <c>jupyter</c> subcommand: <c>[connection_file, [refs_file]]</c>.
    /// Kernel-spec queries (<c>--kernel-spec-path</c> / <c>--kernel-spec-details</c>)
    /// are answered without starting a kernel.
    /// </summary>
    public static void Run(string[] args, ILoggerFactory loggerFactory) {
        // Handle requests for kernel-spec information.
        if (KernelSpec.HandleKernelSpecRequest(args)) {
            return;
        }

        // When Jupyter starts a kernel, it passes a connection file describing
        // how to set up communication with the frontend.
        Console.WriteLine("Kernel connecting...");
        for (int i = 0; i < args.Length; i++) {
            Console.WriteLine($"arg {i}: {args[i]}");
        }

        // Create the connection model.
        string json = File.ReadAllText(args[0]);
        var connInfo = ProtocolJson.Deserialize<ConnInfo>(json);
        Console.WriteLine(ProtocolJson.Serialize(connInfo));

        if (args.Length > 1) {
            InteractiveScriptEngine.RefsFilePath = args[1];
        }

        // After reading the connection file and binding sockets, the kernel
        // enters its event loop, listening on the hb (heartbeat), control and
        // shell sockets.
        var kernel = new Kernel(connInfo, loggerFactory);
        kernel.Start();
    }
}
