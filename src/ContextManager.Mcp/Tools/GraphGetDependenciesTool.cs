using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphGetDependenciesTool
{
    private readonly GraphRegistry _registry;
    private readonly IGraphSynchronizer? _synchronizer;

    public GraphGetDependenciesTool(GraphRegistry registry)
    {
        _registry = registry;
    }

    [ActivatorUtilitiesConstructor]
    public GraphGetDependenciesTool(GraphRegistry registry, IGraphSynchronizer synchronizer)
    {
        _registry = registry;
        _synchronizer = synchronizer;
    }

    [McpServerTool(Name = "graph_get_dependencies"), Description("Return the aggregated in-and-out neighbors of a graph node. " + GraphSyncRunner.QuerySynchronizationDescription + " Type queries include the edges of the type's own members, so consumers that only call into its methods still appear; neighbor member nodes are rolled up to their declaring type. Each entry carries: id (rolled-up node ID), kind (Class/Interface/Record), direction (\"out\" = the queried node depends on it, \"in\" = it depends on the queried node), edgeKinds (per-edge-kind counts, e.g. {\"CALLS\": 3, \"REFERENCES\": 1}), and file (the neighbor's declaring source file, when known, ready for inspect_file). One entry is emitted per (id, direction). The queried node's own CONTAINS member edges are excluded — use inspect_file for a type's members. Output order is out-entries first, then in-entries, in graph-encounter order.")]
    public async Task<string> GraphGetDependenciesAsync(
        [Description(GraphSyncRunner.QuerySolutionPathDescription)] string solutionPath,
        [Description("The ISymbol.ToDisplayString() ID of the node whose neighbors to retrieve.")] string nodeId,
        CancellationToken ct = default)
    {
        var (store, error) = await GraphSyncRunner.LoadForQueryAsync(_registry, _synchronizer, solutionPath, "graph_get_dependencies", ct);
        if (store is null)
            return error!;

        if (!store.TryGetNode(nodeId, out _))
            return JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options);

        var neighbors = store.GetAggregatedNeighbors(nodeId, ct);
        var contracts = neighbors.Select(n => new GraphNodeContract(n.Id, n.Kind, n.Direction, n.EdgeKinds, n.File)).ToList();

        return JsonSerializer.Serialize(contracts, AnalysisJson.Options);
    }
}
