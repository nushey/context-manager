using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuikGraph;
using QuikGraph.Algorithms;

namespace ContextManager.Analysis.Graph;

public class GraphStore
{
    // Same options as SerializerOptions in the Mcp layer — kept here to avoid a circular dependency.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private BidirectionalGraph<GraphNode, GraphEdge> _graph = new(allowParallelEdges: false);

    // Index for fast lookup by ID without scanning all vertices.
    private readonly Dictionary<string, GraphNode> _nodeById = new(StringComparer.Ordinal);

    public int NodeCount => _graph.VertexCount;
    public int EdgeCount => _graph.EdgeCount;

    public void AddNode(GraphNode node)
    {
        if (_nodeById.ContainsKey(node.Id))
            return;

        _graph.AddVertex(node);
        _nodeById[node.Id] = node;
    }

    public void AddEdge(GraphEdge edge)
    {
        // Ensure both endpoints exist before adding the edge.
        AddNode(edge.Source);
        AddNode(edge.Target);

        _graph.AddEdge(edge);
    }

    public bool TryGetNode(string id, out GraphNode? node) => _nodeById.TryGetValue(id, out node);

    public IReadOnlyList<GraphNode> GetNeighbors(string nodeId)
    {
        if (!_nodeById.TryGetValue(nodeId, out var node))
            return [];

        var neighbors = new HashSet<GraphNode>();

        foreach (var edge in _graph.OutEdges(node))
            neighbors.Add(edge.Target);

        foreach (var edge in _graph.InEdges(node))
            neighbors.Add(edge.Source);

        return [.. neighbors];
    }

    /// <summary>
    /// Breadth-first backward traversal: follows in-edges whose Type is in <paramref name="edgeTypes"/>.
    /// Returns node IDs in BFS visit order, excluding the start node.
    /// </summary>
    public IReadOnlyList<string> BfsBackward(string nodeId, IReadOnlySet<string> edgeTypes)
    {
        if (!_nodeById.TryGetValue(nodeId, out var startNode))
            return [];

        var visited = new HashSet<GraphNode> { startNode };
        var queue = new Queue<GraphNode>();
        var result = new List<string>();

        queue.Enqueue(startNode);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var edge in _graph.InEdges(current))
            {
                if (!edgeTypes.Contains(edge.Type))
                    continue;

                if (!visited.Add(edge.Source))
                    continue;

                result.Add(edge.Source.Id);
                queue.Enqueue(edge.Source);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the ordered node IDs forming the directed shortest path from source to target
    /// (inclusive of both endpoints). Returns empty if no path exists.
    /// </summary>
    public IReadOnlyList<string> ShortestPath(string sourceId, string targetId)
    {
        if (!_nodeById.TryGetValue(sourceId, out var sourceNode))
            return [];

        if (!_nodeById.TryGetValue(targetId, out var targetNode))
            return [];

        if (sourceNode.Equals(targetNode))
            return [sourceId];

        var tryGetPaths = _graph.ShortestPathsDijkstra(_ => 1.0, sourceNode);

        if (!tryGetPaths(targetNode, out var edges))
            return [];

        var path = new List<string> { sourceId };
        foreach (var edge in edges)
            path.Add(edge.Target.Id);

        return path;
    }

    /// <summary>
    /// Serializes the graph to JSON: <c>{ "nodes": [...], "edges": [...] }</c>.
    /// Uses <see cref="SerializerOptions"/> for consistency with existing tools.
    /// </summary>
    public string Serialize()
    {
        var payload = new GraphPayload(
            Nodes: _graph.Vertices.Select(n => new NodeDto(n.Id, n.Kind)).ToList(),
            Edges: _graph.Edges.Select(e => new EdgeDto(e.Source.Id, e.Target.Id, e.Type)).ToList());

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>
    /// Replaces the current in-memory graph with the one deserialized from <paramref name="json"/>.
    /// </summary>
    public void Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<GraphPayload>(json, SerializerOptions)
            ?? throw new JsonException("Graph payload deserialized to null.");

        Clear();

        foreach (var n in payload.Nodes)
            AddNode(new GraphNode(n.Id, n.Kind));

        foreach (var e in payload.Edges)
        {
            if (_nodeById.TryGetValue(e.Source, out var src) &&
                _nodeById.TryGetValue(e.Target, out var tgt))
            {
                _graph.AddEdge(new GraphEdge(src, tgt, e.Type));
            }
        }
    }

    public void Clear()
    {
        _graph = new BidirectionalGraph<GraphNode, GraphEdge>(allowParallelEdges: false);
        _nodeById.Clear();
    }

    // Private DTOs used only for serialization — never exposed outside this class.
    private sealed record GraphPayload(List<NodeDto> Nodes, List<EdgeDto> Edges);
    private sealed record NodeDto(string Id, string Kind);
    private sealed record EdgeDto(string Source, string Target, string Type);
}
