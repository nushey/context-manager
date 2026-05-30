namespace ContextManager.Analysis.Graph;

/// <summary>
/// A neighbor node annotated with the connecting edge type and direction.
/// One entry is emitted per (neighbor, edgeType, direction) so that the same
/// neighbor reachable via two different edge kinds appears as two distinct entries.
/// </summary>
public sealed record GraphNeighbor(string Id, string Kind, string Type, string Direction);
