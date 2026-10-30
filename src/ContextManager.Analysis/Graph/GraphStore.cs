using System.Collections.Immutable;
using System.Text.Json;

namespace ContextManager.Analysis.Graph;

public class GraphStore
{
    private readonly record struct Position(int Project, int Document, int Local) : IComparable<Position>
    {
        public int CompareTo(Position other)
        {
            var byProject = Project.CompareTo(other.Project);
            if (byProject != 0)
                return byProject;

            var byDocument = Document.CompareTo(other.Document);
            return byDocument != 0 ? byDocument : Local.CompareTo(other.Local);
        }
    }

    private readonly record struct EdgeKey(string Source, string Target, string Type);

    private sealed class NodeEntry
    {
        public NodeEntry(
            ImmutableSortedDictionary<Position, GraphNode> declarations,
            ImmutableSortedDictionary<Position, GraphNode> references)
        {
            Declarations = declarations;
            References = references;
            Node = (declarations.IsEmpty ? references : declarations).First().Value;
            First = declarations.IsEmpty ? references.Keys.First()
                : references.IsEmpty ? declarations.Keys.First()
                : Min(declarations.Keys.First(), references.Keys.First());
        }

        public ImmutableSortedDictionary<Position, GraphNode> Declarations { get; }
        public ImmutableSortedDictionary<Position, GraphNode> References { get; }
        public GraphNode Node { get; }
        public Position First { get; }

        private static Position Min(Position a, Position b) => a.CompareTo(b) <= 0 ? a : b;
    }

    private sealed class EdgeEntry(ImmutableSortedSet<Position> owners)
    {
        public ImmutableSortedSet<Position> Owners { get; } = owners;
        public Position First { get; } = owners.Min;
    }

    private sealed record State(
        ImmutableDictionary<string, NodeEntry> Nodes,
        ImmutableSortedDictionary<Position, string> NodeOrder,
        ImmutableDictionary<EdgeKey, EdgeEntry> Edges,
        ImmutableDictionary<string, ImmutableSortedDictionary<Position, EdgeKey>> Out,
        ImmutableDictionary<string, ImmutableSortedDictionary<Position, EdgeKey>> In,
        ImmutableDictionary<string, ImmutableSortedDictionary<Position, string>> OwnersByMember,
        ImmutableDictionary<(string ProjectKey, string Path), GraphDocumentRecord> Documents,
        int NextAdHocPosition,
        bool HasProvenance,
        string? Revision,
        GraphBaseline? Baseline)
    {
        public static readonly State Empty = new(
            ImmutableDictionary.Create<string, NodeEntry>(StringComparer.Ordinal),
            ImmutableSortedDictionary<Position, string>.Empty,
            ImmutableDictionary<EdgeKey, EdgeEntry>.Empty,
            ImmutableDictionary.Create<string, ImmutableSortedDictionary<Position, EdgeKey>>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, ImmutableSortedDictionary<Position, EdgeKey>>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, ImmutableSortedDictionary<Position, string>>(StringComparer.Ordinal),
            ImmutableDictionary<(string ProjectKey, string Path), GraphDocumentRecord>.Empty,
            0,
            false,
            null,
            null);
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
    public int EdgeCount => _state.Edges.Count;

    internal bool HasProvenance => _state.HasProvenance;
    internal string? Revision => _state.Revision;
    internal GraphBaseline? Baseline => _state.Baseline;

    internal IReadOnlyDictionary<(string ProjectKey, string Path), GraphDocumentRecord> Documents => _state.Documents;

    public void AddNode(GraphNode node)
    {
        var mutation = new Mutation(_state);
        mutation.TouchAdHoc(node);
        _state = mutation.ToState();
    }

    public void AddEdge(GraphEdge edge)
    {
        var mutation = new Mutation(_state);
        mutation.AddAdHocEdge(edge);
        _state = mutation.ToState();
    }

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
    /// Prepares a candidate graph by applying a document-contribution delta to the current snapshot
    /// (or to an empty graph when <paramref name="fromEmpty"/> is true). Removed and replaced
    /// documents give up their ownership of nodes and edges; a node or edge disappears only when no
    /// surviving contribution owns it. Nothing is published: pass the candidate to
    /// <see cref="Publish"/> once it has been persisted.
    /// </summary>
    public GraphStore Stage(
        IReadOnlyCollection<(string ProjectKey, string Path)> removedDocuments,
        IReadOnlyCollection<GraphDocumentRecord> upsertedDocuments,
        GraphBaseline baseline,
        bool fromEmpty)
    {
        var mutation = new Mutation(fromEmpty ? State.Empty : _state);
        foreach (var key in removedDocuments)
            mutation.RemoveDocument(key);

        foreach (var record in upsertedDocuments)
            mutation.RemoveDocument(record.Key);

        foreach (var record in upsertedDocuments)
            mutation.AddDocument(record);

        return new GraphStore(mutation.ToState() with
        {
            HasProvenance = true,
            Revision = Guid.NewGuid().ToString("N"),
            Baseline = baseline
        });
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
    /// Breadth-first backward traversal: follows in-edges whose Type is in <paramref name="edgeTypes"/>.
    /// Returns node IDs in BFS visit order, excluding the start node.
    /// </summary>
    public IReadOnlyList<string> BfsBackward(string nodeId, IReadOnlySet<string> edgeTypes, CancellationToken ct = default)
    {
        var s = _state;
        if (!s.Nodes.ContainsKey(nodeId))
            return [];

        var visited = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var queue = new Queue<string>();
        var result = new List<string>();

        queue.Enqueue(nodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ct.ThrowIfCancellationRequested();

            foreach (var edge in InEdges(s, current))
            {
                if (!edgeTypes.Contains(edge.Type))
                    continue;

                if (!visited.Add(edge.Source))
                    continue;

                result.Add(edge.Source);
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
        => ImpactBackwardCore(nodeId, edgeTypes, maxResults: null, out _, out bridgedInterfaceIds, ct);

    /// <summary>
    /// Capped variant of <see cref="ImpactBackward(string, IReadOnlySet{string}, out IReadOnlyList{string}, CancellationToken)"/>.
    /// Runs the full BFS (so visit order and <paramref name="bridgedInterfaceIds"/> stay deterministic)
    /// but stops appending to the returned list once it reaches <paramref name="maxResults"/> entries.
    /// <paramref name="truncated"/> is set to <c>true</c> when at least one additional reachable type
    /// was dropped due to the cap. The returned ids are the BFS-ordered prefix (no reordering).
    /// </summary>
    public IReadOnlyList<string> ImpactBackward(
        string nodeId,
        IReadOnlySet<string> edgeTypes,
        int maxResults,
        out bool truncated,
        out IReadOnlyList<string> bridgedInterfaceIds,
        CancellationToken ct = default)
        => ImpactBackwardCore(nodeId, edgeTypes, maxResults, out truncated, out bridgedInterfaceIds, ct);

    /// <summary>
    /// Overload without the <c>bridgedInterfaceIds</c> out parameter — convenience for callers
    /// that do not need diagnostic data.
    /// </summary>
    public IReadOnlyList<string> ImpactBackward(string nodeId, IReadOnlySet<string> edgeTypes) =>
        ImpactBackward(nodeId, edgeTypes, out _);

    private IReadOnlyList<string> ImpactBackwardCore(
        string nodeId,
        IReadOnlySet<string> edgeTypes,
        int? maxResults,
        out bool truncated,
        out IReadOnlyList<string> bridgedInterfaceIds,
        CancellationToken ct = default)
    {
        var s = _state;
        if (!s.Nodes.ContainsKey(nodeId))
        {
            truncated = false;
            bridgedInterfaceIds = [];
            return [];
        }

        var bridged = new List<string>();
        var hitCap = false;

        // Build seed: start node + its members + implemented interfaces + their members.
        var seeds = new List<string> { nodeId };
        seeds.AddRange(GetContainedMembers(s, nodeId));

        foreach (var iface in GetImplementedInterfaces(s, nodeId))
        {
            seeds.Add(iface);
            seeds.AddRange(GetContainedMembers(s, iface));
            bridged.Add(iface);
        }

        var seedIds = new HashSet<string>(seeds, StringComparer.Ordinal);
        var visited = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        var reportedTypes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // The traversal always visits the full reachable set so visit order (and thus the capped
        // prefix) and bridgedInterfaceIds stay deterministic regardless of maxResults. The cap only
        // bounds the result list; a dropped qualifying type flips `truncated`.
        void Record(string reportId)
        {
            if (seedIds.Contains(reportId) || !reportedTypes.Add(reportId))
                return;

            if (!maxResults.HasValue || result.Count < maxResults.Value)
                result.Add(reportId);
            else
                hitCap = true;
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ct.ThrowIfCancellationRequested();

            // Apply interface bridging to every concrete class dequeued — not only the seed.
            // Enqueue bridged interfaces and their members so inbound INJECTS on those interfaces
            // are followed. The shared visited set guarantees cycle safety and dedup.
            foreach (var iface in GetImplementedInterfaces(s, current))
            {
                if (!visited.Add(iface))
                    continue;

                queue.Enqueue(iface);
                seedIds.Add(iface);
                bridged.Add(iface);

                foreach (var member in GetContainedMembers(s, iface))
                {
                    if (visited.Add(member))
                    {
                        queue.Enqueue(member);
                        seedIds.Add(member);
                    }
                }
            }

            // Inbound IMPLEMENTS: the inverse of outbound bridging. Whenever an interface is
            // dequeued, its implementor classes are real compile-time breakage — they go INTO
            // the result (rolled up if a member) AND are enqueued for full transitive reach.
            // Unlike bridged interfaces, implementors are NOT added to seedIds/bridged, so the
            // reflection diagnostic and result-vs-routing distinction are preserved.
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

        bridgedInterfaceIds = bridged;
        truncated = hitCap;
        return result;
    }

    /// <summary>
    /// Returns the IDs of interface nodes that <paramref name="nodeId"/> implements
    /// (outbound <c>IMPLEMENTS</c> edges). Returns empty if the node is unknown or
    /// has no outbound IMPLEMENTS edges.
    /// </summary>
    public IReadOnlyList<string> GetImplementedInterfaceIds(string nodeId) =>
        GetImplementedInterfaces(_state, nodeId).ToList();

    /// <summary>
    /// Returns the type nodes (Class, Interface, Record) whose ID matches a short or qualified type
    /// name once generic argument lists are ignored: the full ID itself, or any ID ending with
    /// <c>.</c> plus the name. Matches are returned in graph-encounter order.
    /// </summary>
    public IReadOnlyList<GraphNode> TryGetShortNameMatches(string shortOrFullTypeName)
    {
        var s = _state;
        var name = StripGenericArguments(shortOrFullTypeName.Trim());
        if (name.Length == 0)
            return [];

        var suffix = "." + name;
        return s.NodeOrder.Values
            .Select(id => s.Nodes[id].Node)
            .Where(n => NodeClassifier.IsTypeKind(n.Kind))
            .Where(n =>
            {
                var id = StripGenericArguments(n.Id);
                return id == name || id.EndsWith(suffix, StringComparison.Ordinal);
            })
            .ToList();
    }

    private static string StripGenericArguments(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        var depth = 0;
        foreach (var c in name)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
            else if (depth == 0)
                builder.Append(c);
        }

        return builder.ToString();
    }

    // Returns all nodes connected via outbound CONTAINS edges (the direct members of a type).
    private static IEnumerable<string> GetContainedMembers(State state, string typeId) =>
        OutEdges(state, typeId)
            .Where(e => e.Type == "CONTAINS" && GetDeclaringTypeId(state, e.Target) == typeId)
            .Select(e => e.Target);

    // Returns all nodes connected via outbound IMPLEMENTS edges.
    private static IEnumerable<string> GetImplementedInterfaces(State state, string typeId) =>
        OutEdges(state, typeId)
            .Where(e => e.Type == "IMPLEMENTS")
            .Select(e => e.Target);

    // Returns all nodes connected via inbound IMPLEMENTS edges (the classes that implement a type).
    private static IEnumerable<string> GetImplementors(State state, string typeId) =>
        InEdges(state, typeId)
            .Where(e => e.Type == "IMPLEMENTS")
            .Select(e => e.Source);

    // A node is a member if it has at least one inbound CONTAINS edge.
    private static bool IsMemberNode(State state, string nodeId) => state.OwnersByMember.ContainsKey(nodeId);

    // Returns the declaring type of a member node via its inbound CONTAINS edge.
    private static string? GetDeclaringTypeId(State state, string memberId) =>
        state.OwnersByMember.TryGetValue(memberId, out var owners) ? owners.First().Value : null;

    private static GraphNode? NodeOf(State state, string id) =>
        state.Nodes.TryGetValue(id, out var entry) ? entry.Node : null;

    private static IEnumerable<EdgeKey> OutEdges(State state, string id) =>
        state.Out.TryGetValue(id, out var edges) ? edges.Values : [];

    private static IEnumerable<EdgeKey> InEdges(State state, string id) =>
        state.In.TryGetValue(id, out var edges) ? edges.Values : [];

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
            if (NodeOf(s, id) is not { Kind: "Interface" })
                continue;

            if (!GetImplementors(s, id).Any())
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

    /// <summary>
    /// Serializes the graph to JSON: <c>{ "revision", "nodes": [...], "edges": [...] }</c>, plus the
    /// per-document contributions and synchronization baseline when the graph carries provenance.
    /// Uses the shared <see cref="AnalysisJson"/> options for consistency with the MCP tools.
    /// </summary>
    public string Serialize()
    {
        var s = _state;
        var nodes = s.NodeOrder.Values.Select(id => s.Nodes[id].Node).ToList();
        var payload = new GraphPayload(
            Revision: s.Revision,
            Nodes: nodes.Select(n => new NodeDto(n.Id, n.Kind, n.File)).ToList(),
            Edges: nodes.SelectMany(n => OutEdges(s, n.Id)).Select(e => new EdgeDto(e.Source, e.Target, e.Type)).ToList(),
            Documents: s.HasProvenance
                ? s.Documents.Values
                    .OrderBy(d => d.ProjectOrder)
                    .ThenBy(d => d.DocumentOrder)
                    .Select(d => new DocumentDto(
                        d.ProjectKey,
                        d.Path,
                        d.Checksum,
                        d.ProjectOrder,
                        d.DocumentOrder,
                        d.Nodes.Select(n => n.Id).ToList(),
                        d.Edges.Select(e => new EdgeDto(e.Source.Id, e.Target.Id, e.Type)).ToList(),
                        d.Nodes.ToList(),
                        d.Edges.ToList()))
                    .ToList()
                : null,
            Baseline: s.HasProvenance ? s.Baseline : null);

        return JsonSerializer.Serialize(payload, AnalysisJson.Options);
    }

    /// <summary>
    /// Replaces the current in-memory graph with the one deserialized from <paramref name="json"/>.
    /// A payload with document contributions is rebuilt from them and keeps its provenance; a legacy
    /// payload with only nodes and edges stays queryable but carries no provenance. Builds a fresh
    /// snapshot and publishes it atomically; on any throw the previous graph is left untouched.
    /// </summary>
    public void Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<GraphPayload>(json, AnalysisJson.Options)
            ?? throw new JsonException("Graph payload deserialized to null.");

        var nodeById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in payload.Nodes)
            nodeById.TryAdd(n.Id, new GraphNode(n.Id, n.Kind, n.File));

        var mutation = new Mutation(State.Empty);
        if (payload.Documents is null || payload.Documents.Any(d => d.NodeContributions is null || d.EdgeContributions is null))
        {
            foreach (var node in nodeById.Values)
                mutation.TouchAdHoc(node);

            foreach (var e in payload.Edges)
            {
                if (nodeById.TryGetValue(e.Source, out var src) && nodeById.TryGetValue(e.Target, out var tgt))
                    mutation.AddAdHocEdge(new GraphEdge(src, tgt, e.Type));
            }

            _state = mutation.ToState() with { Revision = payload.Revision };
            return;
        }

        foreach (var d in payload.Documents)
        {
            mutation.AddDocument(new GraphDocumentRecord(
                d.Project,
                d.Path,
                d.Checksum,
                d.ProjectOrder,
                d.DocumentOrder,
                d.NodeContributions!,
                d.EdgeContributions!));
        }

        _state = mutation.ToState() with
        {
            HasProvenance = true,
            Revision = payload.Revision,
            Baseline = payload.Baseline
        };
    }

    public void Clear()
    {
        _state = State.Empty;
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

    private sealed class Mutation
    {
        private readonly State _origin;
        private readonly ImmutableDictionary<string, NodeEntry>.Builder _nodes;
        private readonly ImmutableSortedDictionary<Position, string>.Builder _nodeOrder;
        private readonly ImmutableDictionary<EdgeKey, EdgeEntry>.Builder _edges;
        private readonly ImmutableDictionary<string, ImmutableSortedDictionary<Position, EdgeKey>>.Builder _out;
        private readonly ImmutableDictionary<string, ImmutableSortedDictionary<Position, EdgeKey>>.Builder _in;
        private readonly ImmutableDictionary<string, ImmutableSortedDictionary<Position, string>>.Builder _ownersByMember;
        private readonly ImmutableDictionary<(string ProjectKey, string Path), GraphDocumentRecord>.Builder _documents;
        private int _nextAdHocPosition;

        public Mutation(State origin)
        {
            _origin = origin;
            _nodes = origin.Nodes.ToBuilder();
            _nodeOrder = origin.NodeOrder.ToBuilder();
            _edges = origin.Edges.ToBuilder();
            _out = origin.Out.ToBuilder();
            _in = origin.In.ToBuilder();
            _ownersByMember = origin.OwnersByMember.ToBuilder();
            _documents = origin.Documents.ToBuilder();
            _nextAdHocPosition = origin.NextAdHocPosition;
        }

        public State ToState() => _origin with
        {
            Nodes = _nodes.ToImmutable(),
            NodeOrder = _nodeOrder.ToImmutable(),
            Edges = _edges.ToImmutable(),
            Out = _out.ToImmutable(),
            In = _in.ToImmutable(),
            OwnersByMember = _ownersByMember.ToImmutable(),
            Documents = _documents.ToImmutable(),
            NextAdHocPosition = _nextAdHocPosition
        };

        public void TouchAdHoc(GraphNode node) => Touch(node, NextAdHoc(), declared: true, add: true);

        public void AddAdHocEdge(GraphEdge edge)
        {
            Touch(edge.Source, NextAdHoc(), declared: false, add: true);
            Touch(edge.Target, NextAdHoc(), declared: false, add: true);
            ChangeEdgeOwner(new EdgeKey(edge.Source.Id, edge.Target.Id, edge.Type), NextAdHoc(), add: true);
        }

        public void AddDocument(GraphDocumentRecord record)
        {
            _documents[record.Key] = record;
            Visit(record, add: true);
        }

        public void RemoveDocument((string ProjectKey, string Path) key)
        {
            if (!_documents.TryGetValue(key, out var record))
                return;

            _documents.Remove(key);
            Visit(record, add: false);
        }

        private Position NextAdHoc() => new(int.MaxValue, int.MaxValue, _nextAdHocPosition++);

        private void Visit(GraphDocumentRecord record, bool add)
        {
            var local = 0;
            Position Next() => new(record.ProjectOrder, record.DocumentOrder, local++);

            foreach (var node in record.Nodes)
                Touch(node, Next(), declared: true, add);

            foreach (var edge in record.Edges)
            {
                Touch(edge.Source, Next(), declared: false, add);
                Touch(edge.Target, Next(), declared: false, add);
                ChangeEdgeOwner(new EdgeKey(edge.Source.Id, edge.Target.Id, edge.Type), Next(), add);
            }
        }

        private void Touch(GraphNode node, Position position, bool declared, bool add)
        {
            var existing = _nodes.GetValueOrDefault(node.Id);
            var declarations = existing?.Declarations ?? ImmutableSortedDictionary<Position, GraphNode>.Empty;
            var references = existing?.References ?? ImmutableSortedDictionary<Position, GraphNode>.Empty;

            if (declared)
                declarations = add ? declarations.SetItem(position, node) : declarations.Remove(position);
            else
                references = add ? references.SetItem(position, node) : references.Remove(position);

            if (existing is not null)
                _nodeOrder.Remove(existing.First);

            if (declarations.IsEmpty && references.IsEmpty)
            {
                _nodes.Remove(node.Id);
                return;
            }

            var updated = new NodeEntry(declarations, references);
            _nodes[node.Id] = updated;
            _nodeOrder[updated.First] = node.Id;
        }

        private void ChangeEdgeOwner(EdgeKey key, Position position, bool add)
        {
            var existing = _edges.GetValueOrDefault(key);
            var owners = existing?.Owners ?? ImmutableSortedSet<Position>.Empty;
            owners = add ? owners.Add(position) : owners.Remove(position);

            if (existing is not null)
                Unlink(key, existing.First);

            if (owners.IsEmpty)
            {
                _edges.Remove(key);
                return;
            }

            var updated = new EdgeEntry(owners);
            _edges[key] = updated;
            Link(key, updated.First);
        }

        private void Link(EdgeKey key, Position position)
        {
            _out[key.Source] = _out.GetValueOrDefault(key.Source, ImmutableSortedDictionary<Position, EdgeKey>.Empty).SetItem(position, key);
            _in[key.Target] = _in.GetValueOrDefault(key.Target, ImmutableSortedDictionary<Position, EdgeKey>.Empty).SetItem(position, key);
            if (key.Type == "CONTAINS")
                _ownersByMember[key.Target] = _ownersByMember.GetValueOrDefault(key.Target, ImmutableSortedDictionary<Position, string>.Empty).SetItem(position, key.Source);
        }

        private void Unlink(EdgeKey key, Position position)
        {
            Shrink(_out, key.Source, position);
            Shrink(_in, key.Target, position);
            if (key.Type == "CONTAINS")
                Shrink(_ownersByMember, key.Target, position);
        }

        private static void Shrink<T>(
            ImmutableDictionary<string, ImmutableSortedDictionary<Position, T>>.Builder index,
            string id,
            Position position)
        {
            if (!index.TryGetValue(id, out var entries))
                return;

            var remaining = entries.Remove(position);
            if (remaining.IsEmpty)
                index.Remove(id);
            else
                index[id] = remaining;
        }
    }

    // Private DTOs used only for serialization — never exposed outside this class.
    private sealed record GraphPayload(
        string? Revision,
        List<NodeDto> Nodes,
        List<EdgeDto> Edges,
        List<DocumentDto>? Documents = null,
        GraphBaseline? Baseline = null);
    private sealed record NodeDto(string Id, string Kind, string? File = null);
    private sealed record EdgeDto(string Source, string Target, string Type);
    private sealed record DocumentDto(
        string Project,
        string Path,
        string Checksum,
        int ProjectOrder,
        int DocumentOrder,
        List<string> Nodes,
        List<EdgeDto> Edges,
        List<GraphNode>? NodeContributions = null,
        List<GraphEdge>? EdgeContributions = null);
}
