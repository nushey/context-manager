using QuikGraph;

namespace ContextManager.Analysis.Graph;

// Edge types: INJECTS, CALLS, IMPLEMENTS, INHERITS, RETURNS, CONTAINS, REFERENCES
public sealed record GraphEdge(GraphNode Source, GraphNode Target, string Type) : IEdge<GraphNode>;
