using System.Text;
using NetMQ;

namespace ClrKernel.Core.JupyterKernel.Protocols;

/// <summary>
/// Reading a ZeroMQ frame as text.
///
/// <para>
/// Every frame on this wire is UTF-8 — the Jupyter messaging spec says so, and
/// every client sends it that way. <c>NetMQFrame.ConvertToString()</c> does not
/// decode it as UTF-8, despite <c>SendReceiveConstants.DefaultEncoding</c>
/// reporting <c>utf-8</c>: it returns one <c>?</c> per byte, so a cell whose
/// source contains an em-dash arrives as <c>???</c> and runs that way. The file
/// such a cell writes is mojibake too — the loss is on the way *in*, not on the
/// way out, which is why it looked like an output bug for a while.
/// </para>
/// <para>
/// So the encoding is named at every read. The one-argument overload is correct;
/// the no-argument one is the trap, and it is the one that reads naturally.
/// </para>
/// </summary>
internal static class FrameText {
    public static string Utf8(this NetMQFrame frame) =>
        frame.ConvertToString(Encoding.UTF8);
}
