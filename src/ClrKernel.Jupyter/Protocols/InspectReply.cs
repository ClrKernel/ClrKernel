using System.Collections.Generic;

namespace ClrKernel.Jupyter.Protocols;

/// <summary>Jupyter inspect_reply: whether something was found and its MIME bundle.</summary>
public class InspectReply {
    public bool Found { get; set; }

    public Dictionary<string, object> Data { get; set; } = new();

    public Dictionary<string, object> Metadata { get; set; } = new();

    public string Status { get; set; } = "ok";
}
