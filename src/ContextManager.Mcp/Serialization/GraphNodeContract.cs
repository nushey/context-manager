namespace ContextManager.Mcp.Serialization;

/// <summary>
/// A neighbor node returned by <c>graph_get_dependencies</c>, annotated with the
/// connecting edge metadata. <c>Type</c> is one of IMPLEMENTS / INHERITS / INJECTS /
/// CALLS / RETURNS / REFERENCES / CONTAINS. <c>Direction</c> is <c>"out"</c> when the
/// queried node is the edge source, or <c>"in"</c> when it is the edge target.
/// One entry is emitted per (id, type, direction) so that the same neighbor reachable
/// via two edge kinds appears as two distinguishable entries.
/// </summary>
public sealed record GraphNodeContract(string Id, string Kind, string Type, string Direction);
