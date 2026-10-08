using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphGetDependenciesTool
{
    private readonly GraphRegistry _registry;

    public GraphGetDependenciesTool(GraphRegistry registry)
    {
        _registry = registry;
    }

    [McpServerTool(Name = "graph_get_dependencies"), Description("Return the aggregated in-and-out neighbors of a graph node. Type queries include the edges of the type's own members, so consumers that only call into its methods still appear; neighbor member nodes are rolled up to their declaring type. Each entry carries: id (rolled-up node ID), kind (Class/Interface/Record), direction (\"out\" = the queried node depends on it, \"in\" = it depends on the queried node), and edgeKinds (per-edge-kind counts, e.g. {\"CALLS\": 3, \"REFERENCES\": 1}). One entry is emitted per (id, direction). The queried node's own CONTAINS member edges are excluded — use inspect_file for a type's members. Output order is out-entries first, then in-entries, in graph-encounter order.")]
    public Task<string> GraphGetDependenciesAsync(
        [Description("Absolute path to the .sln whose graph to query, the same path passed to project_scan. The graph is read from .context-manager/graph.json next to it and reloaded whenever that file changes.")] string solutionPath,
        [Description("The ISymbol.ToDisplayString() ID of the node whose neighbors to retrieve.")] string nodeId,
        CancellationToken ct = default)
    {
        var store = _registry.TryLoad(solutionPath);
        if (store is null)
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("graph_not_found", $"No graph for {solutionPath}. Run project_scan first.", solutionPath),
                AnalysisJson.Options));

        if (!store.TryGetNode(nodeId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options));

        var neighbors = store.GetAggregatedNeighbors(nodeId, ct);
        var contracts = neighbors.Select(n => new GraphNodeContract(n.Id, n.Kind, n.Direction, n.EdgeKinds)).ToList();

        return Task.FromResult(JsonSerializer.Serialize(contracts, AnalysisJson.Options));
    }
}
