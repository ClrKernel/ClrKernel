using ClrKernel.Core.JupyterKernel.Protocols;

namespace ClrKernel.Core.JupyterKernel.RequestHandlers;

public interface IRequestHandler<T> {
    void Process(Message<T> message);
}
