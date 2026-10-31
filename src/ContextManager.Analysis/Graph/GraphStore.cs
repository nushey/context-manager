using System.Text.Json;

namespace ContextManager.Analysis.Graph;

public class GraphStore
{
    private readonly record struct EdgeKey(string Source, string Target, string Type);

    private sealed class State
    {
        public static readonly State Empty = new(
            new Dictionary<(string ProjectKey, string Path), GraphDocumentRecord>(),
            null,
            null);

        public State(
            IReadOnlyDictionary<(string ProjectKey, string Path), GraphDocumentRecord> documents,
            string? revision,
            GraphBaseline? baseline)
        {
            Documents = documents;
            Revision = revision;
            Baseline = baseline;

            var references = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            var edges = new HashSet<EdgeKey>();
            foreach (var document in documents.Values.OrderBy(d => d.ProjectOrder).ThenBy(d => d.DocumentOrder))
            {
                foreach (var node in document.Nodes)
                    Nodes.TryAdd(node.Id, node);

                foreach (var edge in document.Edges)
                {
                    references.TryAdd(edge.Source.Id, edge.Source);
                    references.TryAdd(edge.Target.Id, edge.Target);

                    var key = new EdgeKey(edge.Source.Id, edge.Target.Id, edge.Type);
                    if (!edges.Add(key))
                        continue;

                    Append(Out, key.Source, key);
                    Append(In, key.Target, key);
                    if (key.Type == "CONTAINS")
                        OwnerByMember.TryAdd(key.Target, key.Source);
                }
            }

            foreach (var (id, node) in references)
                Nodes.TryAdd(id, node);

            EdgeCount = edges.Count;
        }

        public IReadOnlyDictionary<(string ProjectKey, string Path), GraphDocumentRecord> Documents { get; }
        public string? Revision { get; }
        public GraphBaseline? Baseline { get; }
        public Dictionary<string, GraphNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<EdgeKey>> Out { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<EdgeKey>> In { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> OwnerByMember { get; } = new(StringComparer.Ordinal);
        public int EdgeCount { get; }

        private static void Append(Dictionary<string, List<EdgeKey>> index, string id, EdgeKey key)
        {
            if (!index.TryGetValue(id, out var keys))
                index[id] = keys = [];

            keys.Add(key);
        }
    }

    private volatile State _state;

    public GraphStore()
        : this(State.Empty)
    {
    }

    private GraphStore(State state)
    {
        _state = state;
    }

    public int NodeCount => _state.Nodes.Count;
    public int EdgeCount => _state.EdgeCount;

    internal string? Revision => _state.Revision;
    internal GraphBaseline? Baseline => _state.Baseline;

    internal IReadOnlyDictionary<(string ProjectKey, string Path), GraphDocumentRecord> Documents => _state.Documents;

    public bool TryGetNode(string id, out GraphNode? node)
    {
        node = NodeOf(_state, id);
        return node is not null;
    }

    /// <summary>
    /// Returns a detached store bound to the current snapshot. Every query on the returned store
    /// reads the same graph revision, even if a later update is published on this store.
    /// </summary>
    public GraphStore Capture() => new(_state);

    /// <summary>
    /// Prepares a candidate graph from the current documents without the removed ones and with the
    /// upserted ones replaced or added. Nodes and edges are rebuilt from the documents in project and
    /// document order, so the result matches a fresh scan. Nothing is published: pass the candidate
    /// to <see cref="Publish"/> once it has been persisted.
    /// </summary>
    public GraphStore Stage(
        IReadOnlyCollection<(string ProjectKey, string Path)> removedDocuments,
        IReadOnlyCollection<GraphDocumentRecord> upsertedDocuments,
        GraphBaseline baseline)
    {
        var documents = new Dictionary<(string ProjectKey, string Path), GraphDocumentRecord>(_state.Documents);
        foreach (var key in removedDocuments)
            documents.Remove(key);

        foreach (var record in upsertedDocuments)
            documents[record.Key] = record;

        return new GraphStore(new State(documents, Guid.NewGuid().ToString("N"), baseline));
    }

    public void Publish(GraphStore candidate)
    {
        _state = candidate._state;
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
        if (!s.Nodes.ContainsKey(nodeId))
            return [];

        var scope = new List<string> { nodeId };
        if (!IsMemberNode(s, nodeId))
            scope.AddRange(GetContainedMembers(s, nodeId));

        var scopeSet = new HashSet<string>(scope, StringComparer.Ordinal);
        var entries = new List<(GraphNode Node, string Direction, Dictionary<string, int> EdgeKinds)>();
        var indexByKey = new Dictionary<(string Id, string Direction), int>();

        void Accumulate(string neighborId, string edgeType, string direction)
        {
            var rolledId = IsMemberNode(s, neighborId) ? GetDeclaringTypeId(s, neighborId) ?? neighborId : neighborId;
            var key = (rolledId, direction);

            if (!indexByKey.TryGetValue(key, out var idx))
            {
                idx = entries.Count;
                indexByKey[key] = idx;
                entries.Add((NodeOf(s, rolledId)!, direction, new Dictionary<string, int>(StringComparer.Ordinal)));
            }

            var kinds = entries[idx].EdgeKinds;
            kinds[edgeType] = kinds.TryGetValue(edgeType, out var count) ? count + 1 : 1;
        }

        foreach (var scopeId in scope)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var edge in OutEdges(s, scopeId))
            {
                if (edge.Type == "CONTAINS" || scopeSet.Contains(edge.Target))
                    continue;

                Accumulate(edge.Target, edge.Type, "out");
            }
        }

        foreach (var scopeId in scope)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var edge in InEdges(s, scopeId))
            {
                if (edge.Type == "CONTAINS" || scopeSet.Contains(edge.Source))
                    continue;

                Accumulate(edge.Source, edge.Type, "in");
            }
        }

        return entries
            .Select(e => new GraphNeighbor(e.Node.Id, e.Node.Kind, e.Direction, e.EdgeKinds, e.Node.File))
            .ToList();
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
    /// </summary>
    public IReadOnlyList<string> ImpactBackward(
        string nodeId,
        IReadOnlySet<string> edgeTypes,
        CancellationToken ct = default)
    {
        var s = _state;
        if (!s.Nodes.ContainsKey(nodeId))
            return [];

        var seeds = new List<string> { nodeId };
        seeds.AddRange(GetContainedMembers(s, nodeId));

        foreach (var iface in GetImplementedInterfaces(s, nodeId))
        {
            seeds.Add(iface);
            seeds.AddRange(GetContainedMembers(s, iface));
        }

        var seedIds = new HashSet<string>(seeds, StringComparer.Ordinal);
        var visited = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        var reportedTypes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Record(string reportId)
        {
            if (!seedIds.Contains(reportId) && reportedTypes.Add(reportId))
                result.Add(reportId);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ct.ThrowIfCancellationRequested();

            foreach (var iface in GetImplementedInterfaces(s, current))
            {
                if (!visited.Add(iface))
                    continue;

                queue.Enqueue(iface);
                seedIds.Add(iface);

                foreach (var member in GetContainedMembers(s, iface))
                {
                    if (visited.Add(member))
                    {
                        queue.Enqueue(member);
                        seedIds.Add(member);
                    }
                }
            }

            foreach (var implementor in GetImplementors(s, current))
            {
                if (!visited.Add(implementor))
                    continue;

                queue.Enqueue(implementor);

                var reportId = IsMemberNode(s, implementor)
                    ? GetDeclaringTypeId(s, implementor) ?? implementor
                    : implementor;

                Record(reportId);
            }

            foreach (var edge in InEdges(s, current))
            {
                if (!edgeTypes.Contains(edge.Type))
                    continue;

                var caller = edge.Source;

                if (!visited.Add(caller))
                    continue;

                queue.Enqueue(caller);

                var owner = IsMemberNode(s, caller) ? GetDeclaringTypeId(s, caller) : null;
                var reportId = owner ?? caller;

                Record(reportId);

                if (owner is not null && visited.Add(owner))
                    queue.Enqueue(owner);
            }
        }

        return result;
    }

    private static IEnumerable<string> GetContainedMembers(State state, string typeId) =>
        OutEdges(state, typeId)
            .Where(e => e.Type == "CONTAINS" && GetDeclaringTypeId(state, e.Target) == typeId)
            .Select(e => e.Target);

    private static IEnumerable<string> GetImplementedInterfaces(State state, string typeId) =>
        OutEdges(state, typeId)
            .Where(e => e.Type == "IMPLEMENTS")
            .Select(e => e.Target);

    private static IEnumerable<string> GetImplementors(State state, string typeId) =>
        InEdges(state, typeId)
            .Where(e => e.Type == "IMPLEMENTS")
            .Select(e => e.Source);

    private static bool IsMemberNode(State state, string nodeId) => state.OwnerByMember.ContainsKey(nodeId);

    private static string? GetDeclaringTypeId(State state, string memberId) =>
        state.OwnerByMember.GetValueOrDefault(memberId);

    private static GraphNode? NodeOf(State state, string id) => state.Nodes.GetValueOrDefault(id);

    private static IEnumerable<EdgeKey> OutEdges(State state, string id) =>
        state.Out.TryGetValue(id, out var edges) ? edges : [];

    private static IEnumerable<EdgeKey> InEdges(State state, string id) =>
        state.In.TryGetValue(id, out var edges) ? edges : [];

    /// <summary>
    /// Returns the ordered node IDs forming the directed shortest path from source to target
    /// (inclusive of both endpoints). Returns empty if no path exists.
    /// </summary>
    public IReadOnlyList<string> ShortestPath(string sourceId, string targetId, CancellationToken ct = default)
    {
        var s = _state;
        if (!s.Nodes.ContainsKey(sourceId) || !s.Nodes.ContainsKey(targetId))
            return [];

        if (sourceId == targetId)
            return [sourceId];

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceId };
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        queue.Enqueue(sourceId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            foreach (var edge in OutEdges(s, current))
            {
                if (!visited.Add(edge.Target))
                    continue;

                previous[edge.Target] = current;
                if (edge.Target == targetId)
                    return BuildPath(sourceId, targetId, previous);

                queue.Enqueue(edge.Target);
            }
        }

        return [];
    }

    public string Serialize()
    {
        var s = _state;
        var payload = new GraphPayload(
            s.Revision,
            s.Baseline,
            s.Documents.Values
                .OrderBy(d => d.ProjectOrder)
                .ThenBy(d => d.DocumentOrder)
                .Select(d => new DocumentDto(d.ProjectKey, d.Path, d.Checksum, d.ProjectOrder, d.DocumentOrder, d.Nodes, d.Edges))
                .ToList());

        return JsonSerializer.Serialize(payload, AnalysisJson.Options);
    }

    /// <summary>
    /// Replaces the current in-memory graph with the one deserialized from <paramref name="json"/>.
    /// A payload without documents loads as an empty graph. On any throw the previous graph is left
    /// untouched.
    /// </summary>
    public void Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<GraphPayload>(json, AnalysisJson.Options)
            ?? throw new JsonException("Graph payload deserialized to null.");

        if (payload.Documents is null || payload.Baseline is null)
        {
            _state = State.Empty;
            return;
        }

        var documents = payload.Documents
            .Select(d => new GraphDocumentRecord(d.Project, d.Path, d.Checksum, d.ProjectOrder, d.DocumentOrder, d.Nodes, d.Edges))
            .ToDictionary(d => d.Key);
        _state = new State(documents, payload.Revision, payload.Baseline);
    }

    private static IReadOnlyList<string> BuildPath(
        string source,
        string target,
        IReadOnlyDictionary<string, string> previous)
    {
        var path = new List<string> { target };
        var current = target;
        while (current != source)
        {
            current = previous[current];
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private sealed record GraphPayload(
        string? Revision,
        GraphBaseline? Baseline,
        List<DocumentDto>? Documents);

    private sealed record DocumentDto(
        string Project,
        string Path,
        string Checksum,
        int ProjectOrder,
        int DocumentOrder,
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges);
}
