using System.Collections.Generic;

namespace ClrKernel.Language.PowerShell;

/// <summary>Hover information for a token: markdown plus the span it covers.</summary>
public sealed class PowerShellHover {
    public string Markdown { get; set; }

    /// <summary>Start offset (into the queried text) of the hovered span.</summary>
    public int Start { get; set; }

    /// <summary>Length of the hovered span.</summary>
    public int Length { get; set; }
}

/// <summary>Signature help for a command invocation: one signature per parameter set.</summary>
public sealed class PowerShellSignatureHelp {
    public List<PowerShellSignature> Signatures { get; } = new();
    public int ActiveSignature { get; set; }
    public int ActiveParameter { get; set; }
}

/// <summary>One command signature (a parameter set's syntax).</summary>
public sealed class PowerShellSignature {
    public string Label { get; set; }
    public List<PowerShellParameter> Parameters { get; } = new();
}

/// <summary>One parameter of a signature (e.g. <c>-Path</c>).</summary>
public sealed class PowerShellParameter {
    public string Label { get; set; }
}
