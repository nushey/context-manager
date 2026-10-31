namespace ContextManager.Mcp.Serialization;

public sealed record GraphNodeContract(
    string Id,
    string Kind,
    string Direction,
    IReadOnlyDictionary<string, int> EdgeKinds,
    string? File = null);
