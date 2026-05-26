namespace ContextManager.Mcp.Serialization;

/// <summary>
/// Compressed representation of a graph node returned by graph query tools.
/// </summary>
public sealed record GraphNodeContract(string Id, string Kind);
