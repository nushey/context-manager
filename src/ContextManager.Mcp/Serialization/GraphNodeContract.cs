namespace ContextManager.Mcp.Serialization;

/// <summary>
/// An aggregated neighbor returned by <c>graph_get_dependencies</c>. <c>Id</c> is the
/// rolled-up declaring-type (or standalone node) ID. <c>Direction</c> is <c>"out"</c>
/// when the queried node depends on the neighbor, or <c>"in"</c> when the neighbor
/// depends on the queried node. <c>EdgeKinds</c> maps each edge kind (IMPLEMENTS /
/// INHERITS / INJECTS / CALLS / RETURNS / REFERENCES) to the number of underlying edges
/// aggregated into this entry. One entry is emitted per (id, direction).
/// </summary>
public sealed record GraphNodeContract(
    string Id,
    string Kind,
    string Direction,
    IReadOnlyDictionary<string, int> EdgeKinds);
