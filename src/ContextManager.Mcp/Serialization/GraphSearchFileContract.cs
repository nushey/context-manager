namespace ContextManager.Mcp.Serialization;

public sealed record GraphSearchFileContract(string File, IReadOnlyList<string> Namespaces, bool FileMatched, IReadOnlyList<GraphSearchMethodContract> Methods);
