namespace ContextManager.Analysis.Graph;

public sealed record GraphDocumentRecord(
    string ProjectKey,
    string Path,
    string Checksum,
    int ProjectOrder,
    int DocumentOrder,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<string>? Namespaces = null)
{
    public (string ProjectKey, string Path) Key => (ProjectKey, Path);
}
