namespace ContextManager.Analysis.Graph;

public sealed record GraphSyncResult(
    GraphStore Graph,
    GraphBuildResult Report,
    bool Changed,
    IReadOnlyList<string> ExtractedDocuments);
