namespace ContextManager.Mcp.Serialization;

/// <summary>
/// Token-saving projection of <c>inspect_file</c> output for the <c>compact</c> mode.
/// Each type keeps only its identity and a list of one-liner method signatures
/// (via <c>MethodSignatureFormatter.Format</c>); verbose detail (properties, events,
/// modifiers, line numbers, attributes, constructor dependencies, generic constraints)
/// is omitted. Serialized with the shared <see cref="AnalysisJson"/> options.
/// </summary>
public sealed record CompactFileAnalysis(
    string File,
    string? Namespace,
    IReadOnlyList<CompactTypeInfo> Types,
    IReadOnlyList<string>? ParseErrors = null);

public sealed record CompactTypeInfo(
    string Name,
    string Kind,
    string? Base = null,
    IReadOnlyList<string>? Implements = null,
    IReadOnlyList<string>? Methods = null);
