namespace ContextManager.Analysis.Models;

public sealed record ContextFileAnalysis(
    string File,
    string? Namespace,
    IReadOnlyList<ContextTypeInfo> Types);
