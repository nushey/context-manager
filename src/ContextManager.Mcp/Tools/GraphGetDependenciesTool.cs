using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphGetDependenciesTool
{
    private readonly GraphStore _store;

    public GraphGetDependenciesTool(GraphStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "graph_get_dependencies"), Description("Return the immediate in-and-out neighbors of a graph node as a flat list of neighbor contracts. Each entry carries: id (node ID), kind (Class/Interface/Record/Method/Property), type (edge kind: IMPLEMENTS/INHERITS/INJECTS/CALLS/RETURNS/REFERENCES/CONTAINS), and direction (\"out\" when the queried node is the edge source, \"in\" when it is the edge target). One entry is emitted per (id, type, direction) so the same neighbor reachable via two different edge kinds appears as two distinguishable entries. Output order is out-edges first, then in-edges, in graph-encounter order.")]
    public Task<string> GraphGetDependenciesAsync(
        [Description("The ISymbol.ToDisplayString() ID of the node whose neighbors to retrieve.")] string nodeId,
        CancellationToken ct = default)
    {
        if (!_store.TryGetNode(nodeId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options));

        var neighbors = _store.GetNeighbors(nodeId);
        var contracts = neighbors.Select(n => new GraphNodeContract(n.Id, n.Kind, n.Type, n.Direction)).ToList();

        return Task.FromResult(JsonSerializer.Serialize(contracts, AnalysisJson.Options));
    }
}
