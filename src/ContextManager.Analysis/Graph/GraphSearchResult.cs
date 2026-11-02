namespace ContextManager.Analysis.Graph;

public sealed record GraphSearchResult(IReadOnlyList<GraphSearchFile> Results, bool Truncated);
