namespace ContextManager.Analysis.Graph;

public sealed record GraphProjectState(
    string Key,
    string FilePath,
    string Language,
    bool Loaded,
    string Fingerprint,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<GraphTrackedFile> Documents,
    IReadOnlyList<GraphTrackedFile> AdditionalFiles);
