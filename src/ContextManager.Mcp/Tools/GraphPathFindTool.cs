using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphPathFindTool
{
    private readonly GraphStore _store;

    public GraphPathFindTool(GraphStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "graph_path_find"), Description("Find the directed shortest path between two nodes in the knowledge graph. Returns an ordered list of node IDs from source to target (inclusive). Returns an error if either node is not found or no directed path exists.")]
    public Task<string> GraphPathFindAsync(
        [Description("The ISymbol.ToDisplayString() ID of the source node.")] string sourceId,
        [Description("The ISymbol.ToDisplayString() ID of the target node.")] string targetId,
        CancellationToken ct = default)
    {
        if (!_store.TryGetNode(sourceId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {sourceId}", sourceId),
                AnalysisJson.Options));

        if (!_store.TryGetNode(targetId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {targetId}", targetId),
                AnalysisJson.Options));

        var path = _store.ShortestPath(sourceId, targetId);

        if (path.Count == 0)
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("no_path", "No directed path found between the two nodes.", null),
                AnalysisJson.Options));

        return Task.FromResult(JsonSerializer.Serialize(path, AnalysisJson.Options));
    }
}
