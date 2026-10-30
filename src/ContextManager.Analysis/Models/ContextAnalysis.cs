namespace ContextManager.Analysis.Models;

public sealed record ContextAnalysis(
    IReadOnlyList<ContextFileAnalysis> Files,
    IReadOnlyList<ReferenceInfo> References,
    // BCL types appear here; the compilation has no metadata references by design
    IReadOnlyList<string> Unresolved);
