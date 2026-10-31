namespace ContextManager.Analysis.Graph;

public sealed record GraphBaseline(
    string SolutionPath,
    IReadOnlyList<GraphProjectState> Projects,
    IReadOnlyList<GraphTrackedFile> Inputs,
    IReadOnlyList<string> CandidateFiles,
    bool HasWorkspaceFailures,
    IReadOnlyList<string>? SourceRoots = null);
