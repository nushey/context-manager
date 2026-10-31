namespace ContextManager.Analysis.Graph;

public sealed record GraphNeighbor(
    string Id,
    string Kind,
    string Direction,
    IReadOnlyDictionary<string, int> EdgeKinds,
    string? File = null);
