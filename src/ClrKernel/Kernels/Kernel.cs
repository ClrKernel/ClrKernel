using System;
using System.Threading;
using ClrKernel.Kernels;
using ClrKernel.Protocols;
using ClrKernel.RequestHandlers;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace ClrKernel;
/// <summary>
/// A 'kernel' is a program that runs and introspects the user's code. 
/// https://jupyter-client.readthedocs.io/en/stable/kernels.html
/// </summary>
public class Kernel {
    private ConnInfo _conn;
    private string _shellAddress;
    private string _iopubAddress;
    private string _controlAddress;
    private string _hbAddress;
    private bool _exit = false;
    private KernelInfoHandler<KernelInfoRequest> _kernelInfoHandler;
    private ExecuteHandler<ExecuteRequest> _executeHandler;
    private ILoggerFactory _loggerFactory;

    public Kernel(ConnInfo conn, ILoggerFactory loggerFactory) {
        _conn = conn;
        // https://netmq.readthedocs.io/en/latest/router-dealer/
        _shellAddress = $"@tcp://{conn.IP}:{conn.ShellPort}";
        _iopubAddress = $"@tcp://{conn.IP}:{conn.IOPubPort}";
        _controlAddress = $"@tcp://{conn.IP}:{conn.ControlPort}";
        _hbAddress = $"@tcp://{conn.IP}:{conn.HBPort}";
        _loggerFactory = loggerFactory;
    }

    public void Start() {
        // catch CTRL+C as exit command
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            _exit = true;
        };

        using (var shell = new RouterSocket(_shellAddress))
        using (var iopub = new PublisherSocket(_iopubAddress))
        using (var control = new RouterSocket(_controlAddress))
        using (var heartbeat = new ResponseSocket(_hbAddress))
        using (var poller = new NetMQPoller()) {
            var iopubSender = new MessageSender(_conn.Key, iopub);
            var shellSender = new MessageSender(_conn.Key, shell);
            var controlSender = new MessageSender(_conn.Key, control);
            _kernelInfoHandler = new KernelInfoHandler<KernelInfoRequest>(iopubSender, shellSender);
            _executeHandler = new ExecuteHandler<ExecuteRequest>(iopubSender, shellSender, _loggerFactory);

            // Handler for messages coming in to the frontend
            shell.ReceiveReady += (s, e) => {
                var raw = e.Socket.ReceiveMultipartMessage();
                var header = ProtocolJson.Deserialize<Header>(raw[3].ConvertToString());
                Console.WriteLine($"{header.MessageType}: [{raw.ToString()}]");

                switch (header.MessageType) {
                    case "kernel_info_request": {
                            var message = new Message<KernelInfoRequest>(header, raw);
                            iopubSender.Send(message,
                                new Status { ExecutionState = StatusType.Idle },
                                MessageType.Status);
                            _kernelInfoHandler.Process(message);
                        }
                        break;
                    case "execute_request": {
                            var message = new Message<ExecuteRequest>(header, raw);
                            iopubSender.Send(message,
                                new Status { ExecutionState = StatusType.Busy },
                                MessageType.Status);
                            // Fire-and-forget: do NOT block the poller thread (the
                            // NetMQ sockets belong to it — blocking here deadlocks
                            // sends issued from the script's continuation thread).
                            // ExecuteHandler publishes status:idle itself once the
                            // cell — including any awaited work — has completed, so
                            // clients keep collecting output for async cells.
                            _ = _executeHandler.ProcessAsync(message);
                        }
                        break;
                    case "shutdown_request": {
                            var message = new Message<ShutdownRequest>(header, raw);
                            shellSender.Send(message,
                                new ShutdownReply { Restart = message.Content?.Restart ?? false },
                                MessageType.KernelShutdownReply);
                            _exit = true;
                        }
                        break;
                }
            };

            // Control channel: Jupyter clients (nbclient, papermill, JupyterLab)
            // send shutdown_request here. Without it the kernel had to be
            // force-killed by the client after a timeout.
            control.ReceiveReady += (s, e) => {
                var raw = e.Socket.ReceiveMultipartMessage();
                var header = ProtocolJson.Deserialize<Header>(raw[3].ConvertToString());
                Console.WriteLine($"control {header.MessageType}");

                switch (header.MessageType) {
                    case "shutdown_request": {
                            var message = new Message<ShutdownRequest>(header, raw);
                            controlSender.Send(message,
                                new ShutdownReply { Restart = message.Content?.Restart ?? false },
                                MessageType.KernelShutdownReply);
                            _exit = true;
                        }
                        break;
                    case "kernel_info_request": {
                            var message = new Message<KernelInfoRequest>(header, raw);
                            controlSender.Send(message, new KernelInfoReply(), MessageType.KernelInfoReply);
                        }
                        break;
                }
            };

            // Heartbeat: echo whatever the client pings with, per the protocol.
            heartbeat.ReceiveReady += (s, e) => {
                var frame = e.Socket.ReceiveFrameBytes();
                e.Socket.SendFrame(frame);
            };

            poller.Add(shell);
            poller.Add(control);
            poller.Add(heartbeat);
            poller.RunAsync();

            Console.WriteLine($"Listening Shell {_shellAddress}");
            Console.WriteLine($"Listening IOPub {_iopubAddress}");
            Console.WriteLine($"Listening Control {_controlAddress}");
            Console.WriteLine($"Listening Heartbeat {_hbAddress}");

            // exits on shutdown_request or CTRL+C
            while (!_exit) {
                Thread.Sleep(100);
            }

            poller.Stop();
        }
    }
}
