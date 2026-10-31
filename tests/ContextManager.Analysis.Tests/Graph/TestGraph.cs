using ContextManager.Analysis.Graph;

namespace ContextManager.Analysis.Tests.Graph;

internal static class TestGraph
{
    private static readonly GraphBaseline EmptyBaseline = new("/test/Test.sln", [], [], [], false);

    public static GraphStore WithNode(this GraphStore store, GraphNode node) => store.With([node], []);

    public static GraphStore WithEdge(this GraphStore store, GraphEdge edge) => store.With([], [edge]);

    public static GraphStore With(this GraphStore store, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var order = store.Documents.Count;
        var record = new GraphDocumentRecord("test", $"/test/{order}.cs", string.Empty, 0, order, nodes, edges);
        return store.Stage([], [record], store.Baseline ?? EmptyBaseline);
    }
}
