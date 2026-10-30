namespace ContextManager.Analysis.Graph;

public sealed record GraphSyncTimings(
    TimeSpan Freshness,
    TimeSpan Evaluation,
    TimeSpan Compilation,
    TimeSpan Extraction,
    TimeSpan IndexUpdate,
    TimeSpan Persistence);
