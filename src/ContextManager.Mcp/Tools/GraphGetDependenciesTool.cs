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

    [McpServerTool(Name = "graph_get_dependencies"), Description("Return the immediate in-and-out neighbors of a graph node as compressed node contracts. Each neighbor is represented by its ID and kind; type-kind nodes (Class, Interface, Record) include their kind label. Leaf-kind nodes (Method, Property) include only their ID and kind.")]
    public Task<string> GraphGetDependenciesAsync(
        [Description("The ISymbol.ToDisplayString() ID of the node whose neighbors to retrieve.")] string nodeId,
        CancellationToken ct = default)
    {
        if (!_store.TryGetNode(nodeId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options));

        var neighbors = _store.GetNeighbors(nodeId);
        var contracts = neighbors.Select(n => new GraphNodeContract(n.Id, n.Kind)).ToList();

        return Task.FromResult(JsonSerializer.Serialize(contracts, AnalysisJson.Options));
    }
}
