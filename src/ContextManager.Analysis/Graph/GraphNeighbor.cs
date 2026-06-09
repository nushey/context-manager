namespace ContextManager.Analysis.Graph;

/// <summary>
/// An aggregated neighbor entry: the rolled-up declaring-type (or standalone node) ID,
/// its kind, the direction relative to the queried node, and per-edge-kind counts.
/// One entry exists per (id, direction); a neighbor reachable both inbound and outbound
/// appears as two entries.
/// </summary>
public sealed record GraphNeighbor(
    string Id,
    string Kind,
    string Direction,
    IReadOnlyDictionary<string, int> EdgeKinds);
