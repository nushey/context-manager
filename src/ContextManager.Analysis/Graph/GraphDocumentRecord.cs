namespace ContextManager.Analysis.Graph;

public sealed record GraphDocumentRecord(
    string ProjectKey,
    string Path,
    string Checksum,
    int ProjectOrder,
    int DocumentOrder,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges)
{
    public (string ProjectKey, string Path) Key => (ProjectKey, Path);
}
