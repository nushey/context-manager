namespace ContextManager.Mcp.Serialization;

public sealed record GraphSearchResultContract(IReadOnlyList<GraphSearchFileContract> Results, bool Truncated);
