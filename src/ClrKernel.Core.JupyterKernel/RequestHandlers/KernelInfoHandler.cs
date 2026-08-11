using ClrKernel.Core.JupyterKernel.Kernels;
using ClrKernel.Core.JupyterKernel.Protocols;

namespace ClrKernel.Core.JupyterKernel.RequestHandlers;

public class KernelInfoHandler<T> : IRequestHandler<T> where T : KernelInfoRequest {
    private MessageSender _ioPub;
    private MessageSender _shell;

    public KernelInfoHandler(MessageSender ioPub, MessageSender shell) {
        _ioPub = ioPub;
        _shell = shell;
    }

    public void Process(Message<T> message) {
        _shell.Send(message, new KernelInfoReply(), MessageType.KernelInfoReply);
    }
}
