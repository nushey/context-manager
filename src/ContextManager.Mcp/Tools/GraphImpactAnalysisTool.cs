using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphImpactAnalysisTool
{
    private static readonly IReadOnlySet<string> BackwardEdgeTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };

    private readonly GraphStore _store;

    public GraphImpactAnalysisTool(GraphStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "graph_impact_analysis"), Description(
        "Performs an impact-specific backward traversal from a node following CALLS, INJECTS, REFERENCES, and RETURNS edges. " +
        "Seeds the BFS with the node, its direct members (via CONTAINS), and any interfaces it implements (via IMPLEMENTS) plus their members, " +
        "so member-level dependencies aggregate to their declaring type. " +
        "Member nodes reached during traversal are rolled up to their declaring type. " +
        "Returns an object with 'affectedIds' (ordered list of impacted type IDs) and 'diagnostics' " +
        "(warning entries for interfaces in scope that have zero in-graph implementations — " +
        "a probable reflection or dynamic-dispatch blind spot where the impact set may be incomplete). " +
        "When 'maxResults' is set, 'affectedIds' is capped to that many BFS-ordered entries and a boolean " +
        "'truncated' flag indicates whether additional reachable types were omitted.")]
    public Task<string> GraphImpactAnalysisAsync(
        [Description("The ISymbol.ToDisplayString() ID of the node to analyze for impact.")] string nodeId,
        [Description("Optional cap on the number of affectedIds returned. The full BFS still runs to keep the result deterministic; when the cap is hit, the returned ids are the BFS-ordered prefix and 'truncated' is set to true. Omit for the complete impact set.")] int? maxResults = null,
        CancellationToken ct = default)
    {
        if (!_store.TryGetNode(nodeId, out var startNode))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options));

        IReadOnlyList<string> affectedIds;
        bool truncated;
        IReadOnlyList<string> bridgedInterfaceIds;

        if (maxResults.HasValue)
            affectedIds = _store.ImpactBackward(nodeId, BackwardEdgeTypes, maxResults.Value, out truncated, out bridgedInterfaceIds, ct);
        else
        {
            affectedIds = _store.ImpactBackward(nodeId, BackwardEdgeTypes, out bridgedInterfaceIds, ct);
            truncated = false;
        }

        var diagnostics = BuildReflectionDiagnostics(startNode!, nodeId, bridgedInterfaceIds);
        var result = new GraphImpactResult(affectedIds, diagnostics, truncated);

        return Task.FromResult(JsonSerializer.Serialize(result, AnalysisJson.Options));
    }

    // Collects the interface IDs relevant to this impact analysis: the start node itself
    // (if it is an Interface) plus all interfaces bridged during the full traversal.
    // Using the traversal-derived set (not just start node's direct interfaces) ensures
    // transitive bridging is covered without a second graph walk.
    // Returns diagnostic entries for those with zero in-graph implementations.
    private IReadOnlyList<GraphImpactDiagnostic> BuildReflectionDiagnostics(
        GraphNode startNode,
        string nodeId,
        IReadOnlyList<string> bridgedInterfaceIds)
    {
        var interfacesToCheck = new List<string>();

        if (startNode.Kind == "Interface")
            interfacesToCheck.Add(nodeId);

        // All interfaces bridged during the traversal (seed-time + mid-traversal) are included.
        // This covers transitive bridging discovered mid-BFS, not just the start node's direct interfaces.
        interfacesToCheck.AddRange(bridgedInterfaceIds);

        var blindSpots = _store.GetInterfacesWithNoImplementations(interfacesToCheck);

        return blindSpots
            .Select(id => new GraphImpactDiagnostic(
                "reflection_blind_spot",
                $"Interface '{id}' has no in-graph implementations. " +
                "Its consumers may be wired via reflection or dynamic dispatch and will not appear in the impact set.",
                id))
            .ToList();
    }
}
