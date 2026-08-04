using ClrKernel.Protocols;
using System;
using System.Collections.Generic;
using System.Text;

namespace ClrKernel.RequestHandlers
{
    public interface IRequestHandler<T>
    {
        void Process(Message<T> message);
    }
}
