namespace ContextManager.Analysis.Models;

public sealed record FileAnalysis(
    string File,
    string? Namespace,
    IReadOnlyList<string> Usings,
    IReadOnlyList<TypeInfo> Types);
