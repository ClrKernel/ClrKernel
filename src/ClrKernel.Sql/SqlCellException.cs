using System;

namespace ClrKernel.Sql;
/// <summary>
/// A SQL cell failure surfaced to the notebook as an error output: a syntax
/// error caught before execution, or a server error while running the batch.
/// </summary>
public sealed class SqlCellException : Exception {
    public SqlCellException(string message) : base(message) { }
    public SqlCellException(string message, Exception inner) : base(message, inner) { }
}
