using ClrKernel.Jupyter.Protocols;

namespace ClrKernel.Jupyter.RequestHandlers;

public interface IRequestHandler<T> {
    void Process(Message<T> message);
}
