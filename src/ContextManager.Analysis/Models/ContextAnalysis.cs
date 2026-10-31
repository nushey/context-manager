namespace ContextManager.Analysis.Models;

public sealed record ContextAnalysis(
    IReadOnlyList<ContextFileAnalysis> Files,
    IReadOnlyList<ReferenceInfo> References,
    IReadOnlyList<string> Unresolved);
