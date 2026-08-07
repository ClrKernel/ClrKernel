using System.Collections.Generic;

namespace ClrKernel.Server.Lsp;

// Minimal Language Server Protocol types for the methods ClrKernel implements.
// Serialized with System.Text.Json using a camelCase policy, so PascalCase
// members map to the LSP wire names (uri, textDocument, ...). Only the fields
// ClrKernel reads or writes are modeled.

public sealed class Position {
    public int Line { get; set; }
    public int Character { get; set; }
}

public sealed class Range {
    public Position Start { get; set; }
    public Position End { get; set; }
}

public sealed class TextDocumentIdentifier {
    public string Uri { get; set; }
}

public sealed class TextDocumentItem {
    public string Uri { get; set; }
    public string LanguageId { get; set; }
    public int Version { get; set; }
    public string Text { get; set; }
}

public sealed class TextDocumentContentChangeEvent {
    // Full-sync: only Text is populated (no Range).
    public string Text { get; set; }
}

public sealed class DidOpenTextDocumentParams {
    public TextDocumentItem TextDocument { get; set; }
}

public sealed class DidChangeTextDocumentParams {
    public TextDocumentIdentifier TextDocument { get; set; }
    public List<TextDocumentContentChangeEvent> ContentChanges { get; set; }
}

public sealed class DidCloseTextDocumentParams {
    public TextDocumentIdentifier TextDocument { get; set; }
}

public sealed class TextDocumentPositionParams {
    public TextDocumentIdentifier TextDocument { get; set; }
    public Position Position { get; set; }
}

// --- Completion --------------------------------------------------------

public sealed class CompletionItem {
    public string Label { get; set; }
    public int? Kind { get; set; }
    public string Detail { get; set; }
    public string InsertText { get; set; }
    public string SortText { get; set; }
    public string FilterText { get; set; }
    public TextEdit TextEdit { get; set; }
}

public sealed class TextEdit {
    public Range Range { get; set; }
    public string NewText { get; set; }
}

public sealed class CompletionList {
    public bool IsIncomplete { get; set; }
    public List<CompletionItem> Items { get; set; } = new();
}

// --- Hover -------------------------------------------------------------

public sealed class MarkupContent {
    public string Kind { get; set; } = "markdown";
    public string Value { get; set; }
}

public sealed class Hover {
    public MarkupContent Contents { get; set; }
    public Range Range { get; set; }
}

// --- Signature help ----------------------------------------------------

public sealed class ParameterInformation {
    public string Label { get; set; }
    public MarkupContent Documentation { get; set; }
}

public sealed class SignatureInformation {
    public string Label { get; set; }
    public MarkupContent Documentation { get; set; }
    public List<ParameterInformation> Parameters { get; set; } = new();
}

public sealed class SignatureHelp {
    public List<SignatureInformation> Signatures { get; set; } = new();
    public int ActiveSignature { get; set; }
    public int ActiveParameter { get; set; }
}

// --- Lifecycle / capabilities -----------------------------------------

public sealed class InitializeResult {
    public ServerCapabilities Capabilities { get; set; }
    public ServerInfo ServerInfo { get; set; }
}

public sealed class ServerInfo {
    public string Name { get; set; }
    public string Version { get; set; }
}

public sealed class ServerCapabilities {
    // 1 = full document sync.
    public int TextDocumentSync { get; set; } = 1;
    public CompletionOptions CompletionProvider { get; set; }
    public bool HoverProvider { get; set; }
    public SignatureHelpOptions SignatureHelpProvider { get; set; }
}

public sealed class CompletionOptions {
    public List<string> TriggerCharacters { get; set; }
    public bool ResolveProvider { get; set; }
}

public sealed class SignatureHelpOptions {
    public List<string> TriggerCharacters { get; set; }
}
