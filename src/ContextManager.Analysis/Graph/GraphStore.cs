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

    public IReadOnlyList<GraphNeighbor> GetNeighbors(string nodeId)
    {
        if (!_nodeById.TryGetValue(nodeId, out var node))
            return [];

        var neighbors = new List<GraphNeighbor>();

        foreach (var edge in _graph.OutEdges(node))
            neighbors.Add(new GraphNeighbor(edge.Target.Id, edge.Target.Kind, edge.Type, "out"));

        foreach (var edge in _graph.InEdges(node))
            neighbors.Add(new GraphNeighbor(edge.Source.Id, edge.Source.Kind, edge.Type, "in"));

        return neighbors;
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
    /// Impact-specific backward traversal: seeds the BFS with the start node, its members
    /// (via outbound <c>CONTAINS</c> edges), and any interfaces it implements (via outbound
    /// <c>IMPLEMENTS</c> edges) plus their members. Then follows inbound edges whose type is
    /// in <paramref name="edgeTypes"/>. Member nodes reached during traversal are rolled up to
    /// their declaring type via the member's inbound <c>CONTAINS</c> edge. Interface bridging
    /// is applied continuously — whenever a concrete class is dequeued, its implemented interfaces
    /// (and their members) are fed back into the traversal, guarded by the shared visited set.
    /// Seed nodes and all bridged interfaces/members are excluded from the result.
    /// Returns type IDs in BFS first-visit order, deduplicated.
    /// <paramref name="bridgedInterfaceIds"/> receives all interface IDs bridged during the
    /// traversal (including seed-time bridging) — used by diagnostics without a second graph walk.
    /// </summary>
    public IReadOnlyList<string> ImpactBackward(
        string nodeId,
        IReadOnlySet<string> edgeTypes,
        out IReadOnlyList<string> bridgedInterfaceIds)
    {
        if (!_nodeById.TryGetValue(nodeId, out var startNode))
        {
            bridgedInterfaceIds = [];
            return [];
        }

        var bridged = new List<string>();

        // Build seed: start node + its members + implemented interfaces + their members.
        var seeds = new List<GraphNode> { startNode };
        seeds.AddRange(GetContainedMembers(startNode));

        foreach (var iface in GetImplementedInterfaces(startNode))
        {
            seeds.Add(iface);
            seeds.AddRange(GetContainedMembers(iface));
            bridged.Add(iface.Id);
        }

        var seedIds = new HashSet<string>(seeds.Select(n => n.Id), StringComparer.Ordinal);
        var visited = new HashSet<GraphNode>(seeds);
        var queue = new Queue<GraphNode>(seeds);
        var reportedTypes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Apply interface bridging to every concrete class dequeued — not only the seed.
            // Enqueue bridged interfaces and their members so inbound INJECTS on those interfaces
            // are followed. The shared visited set guarantees cycle safety and dedup.
            foreach (var iface in GetImplementedInterfaces(current))
            {
                if (!visited.Add(iface))
                    continue;

                queue.Enqueue(iface);
                seedIds.Add(iface.Id);
                bridged.Add(iface.Id);

                foreach (var member in GetContainedMembers(iface))
                {
                    if (visited.Add(member))
                    {
                        queue.Enqueue(member);
                        seedIds.Add(member.Id);
                    }
                }
            }

            // Inbound IMPLEMENTS: the inverse of outbound bridging. Whenever an interface is
            // dequeued, its implementor classes are real compile-time breakage — they go INTO
            // the result (rolled up if a member) AND are enqueued for full transitive reach.
            // Unlike bridged interfaces, implementors are NOT added to seedIds/bridged, so the
            // reflection diagnostic and result-vs-routing distinction are preserved.
            foreach (var implementor in GetImplementors(current))
            {
                if (!visited.Add(implementor))
                    continue;

                queue.Enqueue(implementor);

                var reportId = IsMemberNode(implementor)
                    ? GetDeclaringType(implementor)?.Id ?? implementor.Id
                    : implementor.Id;

                if (!seedIds.Contains(reportId) && reportedTypes.Add(reportId))
                    result.Add(reportId);
            }

            foreach (var edge in _graph.InEdges(current))
            {
                if (!edgeTypes.Contains(edge.Type))
                    continue;

                var caller = edge.Source;

                if (!visited.Add(caller))
                    continue;

                queue.Enqueue(caller);

                // Roll up members to their declaring type.
                var reportId = IsMemberNode(caller)
                    ? GetDeclaringType(caller)?.Id ?? caller.Id
                    : caller.Id;

                if (!seedIds.Contains(reportId) && reportedTypes.Add(reportId))
                    result.Add(reportId);
            }
        }

        bridgedInterfaceIds = bridged;
        return result;
    }

    /// <summary>
    /// Overload without the <c>bridgedInterfaceIds</c> out parameter — convenience for callers
    /// that do not need diagnostic data.
    /// </summary>
    public IReadOnlyList<string> ImpactBackward(string nodeId, IReadOnlySet<string> edgeTypes) =>
        ImpactBackward(nodeId, edgeTypes, out _);

    /// <summary>
    /// Returns the IDs of interface nodes that <paramref name="nodeId"/> implements
    /// (outbound <c>IMPLEMENTS</c> edges). Returns empty if the node is unknown or
    /// has no outbound IMPLEMENTS edges.
    /// </summary>
    public IReadOnlyList<string> GetImplementedInterfaceIds(string nodeId)
    {
        if (!_nodeById.TryGetValue(nodeId, out var node))
            return [];

        return _graph.OutEdges(node)
                     .Where(e => e.Type == "IMPLEMENTS")
                     .Select(e => e.Target.Id)
                     .ToList();
    }

    // Returns all nodes connected via outbound CONTAINS edges (the direct members of a type).
    private IEnumerable<GraphNode> GetContainedMembers(GraphNode typeNode) =>
        _graph.OutEdges(typeNode)
              .Where(e => e.Type == "CONTAINS")
              .Select(e => e.Target);

    // Returns all nodes connected via outbound IMPLEMENTS edges.
    private IEnumerable<GraphNode> GetImplementedInterfaces(GraphNode typeNode) =>
        _graph.OutEdges(typeNode)
              .Where(e => e.Type == "IMPLEMENTS")
              .Select(e => e.Target);

    // Returns all nodes connected via inbound IMPLEMENTS edges (the classes that implement a type).
    private IEnumerable<GraphNode> GetImplementors(GraphNode typeNode) =>
        _graph.InEdges(typeNode)
              .Where(e => e.Type == "IMPLEMENTS")
              .Select(e => e.Source);

    // A node is a member if it has at least one inbound CONTAINS edge.
    private bool IsMemberNode(GraphNode node) =>
        _graph.InEdges(node).Any(e => e.Type == "CONTAINS");

    // Returns the declaring type of a member node via its inbound CONTAINS edge.
    private GraphNode? GetDeclaringType(GraphNode memberNode) =>
        _graph.InEdges(memberNode)
              .FirstOrDefault(e => e.Type == "CONTAINS")
              ?.Source;

    /// <summary>
    /// Given a set of interface node IDs, returns those IDs for which no inbound
    /// <c>IMPLEMENTS</c> edge exists in the graph — probable reflection/dynamic blind spots.
    /// Only IDs that actually exist in the graph and whose node kind is <c>Interface</c>
    /// are checked; unknown or non-interface IDs are silently skipped.
    /// </summary>
    public IReadOnlyList<string> GetInterfacesWithNoImplementations(IEnumerable<string> interfaceIds)
    {
        var result = new List<string>();

        foreach (var id in interfaceIds)
        {
            if (!_nodeById.TryGetValue(id, out var node))
                continue;

            if (node.Kind != "Interface")
                continue;

            var hasImplementation = _graph.InEdges(node).Any(e => e.Type == "IMPLEMENTS");
            if (!hasImplementation)
                result.Add(id);
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
