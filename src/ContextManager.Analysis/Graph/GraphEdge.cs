using QuikGraph;

namespace ContextManager.Analysis.Graph;

// Edge types: INJECTS, CALLS, IMPLEMENTS, INHERITS, RETURNS
public sealed record GraphEdge(GraphNode Source, GraphNode Target, string Type) : IEdge<GraphNode>;
