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

    // Immutable-once-published snapshot: the graph and its id index move together so a reader
    // that captures one _state reference observes a consistent pair of collections. A rebuild
    // constructs a separate staging State and publishes it atomically via CommitRebuild, so
    // concurrent readers never enumerate a structure being mutated.
    private sealed class State
    {
        public readonly BidirectionalGraph<GraphNode, GraphEdge> Graph = new(allowParallelEdges: false);
        public readonly Dictionary<string, GraphNode> NodeById = new(StringComparer.Ordinal);
    }

    // Volatile: every reader captures this reference once and reads both collections from it.
    private volatile State _state = new();

    // Staging target for an in-progress rebuild. Volatile because GraphBuilder.BuildAsync is async
    // and may resume on a different thread pool thread after an await — the writer must observe the
    // staging reference set by BeginRebuild. Only the writer (holding _rebuildLock) mutates it.
    private volatile State? _staging;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    public int NodeCount => _state.Graph.VertexCount;
    public int EdgeCount => _state.Graph.EdgeCount;

    public void AddNode(GraphNode node)
    {
        // Route to staging when a rebuild is active; otherwise mutate the live _state.
        // Direct mutation of _state (no rebuild) is reserved for programmatic/test builds,
        // which are single-threaded by contract — concurrent rebuilds go through BeginRebuild.
        var s = _staging ?? _state;
        if (s.NodeById.ContainsKey(node.Id))
            return;

        s.Graph.AddVertex(node);
        s.NodeById[node.Id] = node;
    }

    public void AddEdge(GraphEdge edge)
    {
        // Ensure both endpoints exist before adding the edge.
        AddNode(edge.Source);
        AddNode(edge.Target);

        (_staging ?? _state).Graph.AddEdge(edge);
    }

    public bool TryGetNode(string id, out GraphNode? node) => _state.NodeById.TryGetValue(id, out node);

    /// <summary>
    /// Acquires the writer lock and starts a fresh staging graph. Callers MUST pair this with
    /// <see cref="CommitRebuild"/> (success) or <see cref="AbortRebuild"/> (failure). AddNode/AddEdge
    /// invoked while a rebuild is active mutate the staging graph, leaving the published
    /// <c>_state</c> untouched until CommitRebuild publishes the new snapshot.
    /// </summary>
    public void BeginRebuild()
    {
        _rebuildLock.Wait();
        _staging = new State();
    }

    /// <summary>
    /// Publishes the staging graph atomically and releases the writer lock. Concurrent readers
    /// switch to the new snapshot on their next capture; the previous snapshot stays intact.
    /// No-op (and does not release the lock) if no rebuild is in progress.
    /// </summary>
    public void CommitRebuild()
    {
        var staging = _staging;
        if (staging is null)
            return;

        _staging = null;
        _state = staging; // volatile publish — readers acquire the new snapshot on next read
        _rebuildLock.Release();
    }

    /// <summary>
    /// Discards the staging graph without publishing and releases the writer lock. Use when a
    /// rebuild (GraphBuilder.BuildAsync) fails partway through, so the previous graph is preserved
    /// and the lock is not leaked (which would deadlock the next scan).
    /// </summary>
    public void AbortRebuild()
    {
        var staging = _staging;
        if (staging is null)
            return;

        _staging = null;
        _rebuildLock.Release();
    }

    /// <summary>
    /// Aggregated, type-aware neighbor query. For a type node the aggregation scope is the
    /// node plus its <c>CONTAINS</c> members; for a member node it is the member alone.
    /// Every in/out edge of the scope is rolled up to the neighbor's declaring type and
    /// counted per edge kind. <c>CONTAINS</c> edges and neighbors inside the scope are
    /// excluded entirely (structural noise — <c>inspect_file</c> is the canonical member
    /// source). Returns one entry per (rolled-up id, direction): out entries first, then
    /// in entries, each in graph-encounter order.
    /// </summary>
    public IReadOnlyList<GraphNeighbor> GetAggregatedNeighbors(string nodeId, CancellationToken ct = default)
    {
        var s = _state;
        if (!s.NodeById.TryGetValue(nodeId, out var node))
            return [];

        var graph = s.Graph;
        var scope = new List<GraphNode> { node! };
        if (!IsMemberNode(graph, node!))
            scope.AddRange(GetContainedMembers(graph, node!));

        var scopeSet = new HashSet<GraphNode>(scope);
        var entries = new List<(string Id, string Kind, string Direction, Dictionary<string, int> EdgeKinds)>();
        var indexByKey = new Dictionary<(string Id, string Direction), int>();

        void Accumulate(GraphNode neighbor, string edgeType, string direction)
        {
            var rolled = IsMemberNode(graph, neighbor) ? GetDeclaringType(graph, neighbor) ?? neighbor : neighbor;
            var key = (rolled.Id, direction);

            if (!indexByKey.TryGetValue(key, out var idx))
            {
                idx = entries.Count;
                indexByKey[key] = idx;
                entries.Add((rolled.Id, rolled.Kind, direction, new Dictionary<string, int>(StringComparer.Ordinal)));
            }

            var kinds = entries[idx].EdgeKinds;
            kinds[edgeType] = kinds.TryGetValue(edgeType, out var count) ? count + 1 : 1;
        }

        foreach (var scopeNode in scope)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var edge in graph.OutEdges(scopeNode))
            {
                if (edge.Type == "CONTAINS" || scopeSet.Contains(edge.Target))
                    continue;

                Accumulate(edge.Target, edge.Type, "out");
            }
        }

        foreach (var scopeNode in scope)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var edge in graph.InEdges(scopeNode))
            {
                if (edge.Type == "CONTAINS" || scopeSet.Contains(edge.Source))
                    continue;

                Accumulate(edge.Source, edge.Type, "in");
            }
        }

        return entries
            .Select(e => new GraphNeighbor(e.Id, e.Kind, e.Direction, e.EdgeKinds))
            .ToList();
    }

    /// <summary>
    /// Breadth-first backward traversal: follows in-edges whose Type is in <paramref name="edgeTypes"/>.
    /// Returns node IDs in BFS visit order, excluding the start node.
    /// </summary>
    public IReadOnlyList<string> BfsBackward(string nodeId, IReadOnlySet<string> edgeTypes, CancellationToken ct = default)
    {
        var s = _state;
        if (!s.NodeById.TryGetValue(nodeId, out var startNode))
            return [];

        var graph = s.Graph;
        var visited = new HashSet<GraphNode> { startNode };
        var queue = new Queue<GraphNode>();
        var result = new List<string>();

        queue.Enqueue(startNode);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ct.ThrowIfCancellationRequested();

            foreach (var edge in graph.InEdges(current))
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
        out IReadOnlyList<string> bridgedInterfaceIds,
        CancellationToken ct = default)
    {
        var s = _state;
        if (!s.NodeById.TryGetValue(nodeId, out var startNode))
        {
            bridgedInterfaceIds = [];
            return [];
        }

        var graph = s.Graph;
        var bridged = new List<string>();

        // Build seed: start node + its members + implemented interfaces + their members.
        var seeds = new List<GraphNode> { startNode };
        seeds.AddRange(GetContainedMembers(graph, startNode));

        foreach (var iface in GetImplementedInterfaces(graph, startNode))
        {
            seeds.Add(iface);
            seeds.AddRange(GetContainedMembers(graph, iface));
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
            ct.ThrowIfCancellationRequested();

            // Apply interface bridging to every concrete class dequeued — not only the seed.
            // Enqueue bridged interfaces and their members so inbound INJECTS on those interfaces
            // are followed. The shared visited set guarantees cycle safety and dedup.
            foreach (var iface in GetImplementedInterfaces(graph, current))
            {
                if (!visited.Add(iface))
                    continue;

                queue.Enqueue(iface);
                seedIds.Add(iface.Id);
                bridged.Add(iface.Id);

                foreach (var member in GetContainedMembers(graph, iface))
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
            foreach (var implementor in GetImplementors(graph, current))
            {
                if (!visited.Add(implementor))
                    continue;

                queue.Enqueue(implementor);

                var reportId = IsMemberNode(graph, implementor)
                    ? GetDeclaringType(graph, implementor)?.Id ?? implementor.Id
                    : implementor.Id;

                if (!seedIds.Contains(reportId) && reportedTypes.Add(reportId))
                    result.Add(reportId);
            }

            foreach (var edge in graph.InEdges(current))
            {
                if (!edgeTypes.Contains(edge.Type))
                    continue;

                var caller = edge.Source;

                if (!visited.Add(caller))
                    continue;

                queue.Enqueue(caller);

                // Roll up members to their declaring type.
                var reportId = IsMemberNode(graph, caller)
                    ? GetDeclaringType(graph, caller)?.Id ?? caller.Id
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
        var s = _state;
        if (!s.NodeById.TryGetValue(nodeId, out var node))
            return [];

        return s.Graph.OutEdges(node)
                     .Where(e => e.Type == "IMPLEMENTS")
                     .Select(e => e.Target.Id)
                     .ToList();
    }

    // Returns all nodes connected via outbound CONTAINS edges (the direct members of a type).
    private static IEnumerable<GraphNode> GetContainedMembers(
        BidirectionalGraph<GraphNode, GraphEdge> graph, GraphNode typeNode) =>
        graph.OutEdges(typeNode)
              .Where(e => e.Type == "CONTAINS")
              .Select(e => e.Target);

    // Returns all nodes connected via outbound IMPLEMENTS edges.
    private static IEnumerable<GraphNode> GetImplementedInterfaces(
        BidirectionalGraph<GraphNode, GraphEdge> graph, GraphNode typeNode) =>
        graph.OutEdges(typeNode)
              .Where(e => e.Type == "IMPLEMENTS")
              .Select(e => e.Target);

    // Returns all nodes connected via inbound IMPLEMENTS edges (the classes that implement a type).
    private static IEnumerable<GraphNode> GetImplementors(
        BidirectionalGraph<GraphNode, GraphEdge> graph, GraphNode typeNode) =>
        graph.InEdges(typeNode)
              .Where(e => e.Type == "IMPLEMENTS")
              .Select(e => e.Source);

    // A node is a member if it has at least one inbound CONTAINS edge.
    private static bool IsMemberNode(
        BidirectionalGraph<GraphNode, GraphEdge> graph, GraphNode node) =>
        graph.InEdges(node).Any(e => e.Type == "CONTAINS");

    // Returns the declaring type of a member node via its inbound CONTAINS edge.
    private static GraphNode? GetDeclaringType(
        BidirectionalGraph<GraphNode, GraphEdge> graph, GraphNode memberNode) =>
        graph.InEdges(memberNode)
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
        var s = _state;
        var result = new List<string>();

        foreach (var id in interfaceIds)
        {
            if (!s.NodeById.TryGetValue(id, out var node))
                continue;

            if (node.Kind != "Interface")
                continue;

            var hasImplementation = s.Graph.InEdges(node).Any(e => e.Type == "IMPLEMENTS");
            if (!hasImplementation)
                result.Add(id);
        }

        return result;
    }

    /// <summary>
    /// Returns the ordered node IDs forming the directed shortest path from source to target
    /// (inclusive of both endpoints). Returns empty if no path exists.
    /// </summary>
    public IReadOnlyList<string> ShortestPath(string sourceId, string targetId, CancellationToken ct = default)
    {
        var s = _state;
        if (!s.NodeById.TryGetValue(sourceId, out var sourceNode))
            return [];

        if (!s.NodeById.TryGetValue(targetId, out var targetNode))
            return [];

        if (sourceNode.Equals(targetNode))
            return [sourceId];

        // QuikGraph's Dijkstra is not internally cancellable, so we check once before invoking it.
        // A mid-traversal cancel cannot abort the algorithm; this pre-check catches an already-
        // cancelled client before spending the work.
        ct.ThrowIfCancellationRequested();

        var tryGetPaths = s.Graph.ShortestPathsDijkstra(_ => 1.0, sourceNode);

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
        var s = _state;
        var payload = new GraphPayload(
            Nodes: s.Graph.Vertices.Select(n => new NodeDto(n.Id, n.Kind)).ToList(),
            Edges: s.Graph.Edges.Select(e => new EdgeDto(e.Source.Id, e.Target.Id, e.Type)).ToList());

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>
    /// Replaces the current in-memory graph with the one deserialized from <paramref name="json"/>.
    /// Builds into a fresh local State and publishes it atomically; on any throw the previous
    /// graph is left untouched (no half-cleared store).
    /// </summary>
    public void Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<GraphPayload>(json, SerializerOptions)
            ?? throw new JsonException("Graph payload deserialized to null.");

        var newState = new State();

        foreach (var n in payload.Nodes)
        {
            var node = new GraphNode(n.Id, n.Kind);
            newState.Graph.AddVertex(node);
            newState.NodeById[n.Id] = node;
        }

        foreach (var e in payload.Edges)
        {
            if (newState.NodeById.TryGetValue(e.Source, out var src) &&
                newState.NodeById.TryGetValue(e.Target, out var tgt))
            {
                newState.Graph.AddEdge(new GraphEdge(src, tgt, e.Type));
            }
        }

        _state = newState; // atomic publish
    }

    public void Clear()
    {
        _staging = null;
        _state = new State();
    }

    // Private DTOs used only for serialization — never exposed outside this class.
    private sealed record GraphPayload(List<NodeDto> Nodes, List<EdgeDto> Edges);
    private sealed record NodeDto(string Id, string Kind);
    private sealed record EdgeDto(string Source, string Target, string Type);
}
