using ClrKernel.Protocols;

namespace ClrKernel.RequestHandlers;

public interface IRequestHandler<T> {
    void Process(Message<T> message);
}
