namespace ContextManager.Analysis.Graph;

public sealed record GraphBuildResult(
    string Status,
    int TotalProjects,
    int LoadedProjects,
    int UnsupportedProjects,
    int SkippedProjects,
    int TotalDocuments,
    int LoadedDocuments,
    int SkippedDocuments,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<GraphBuildDiagnostic> Diagnostics);
