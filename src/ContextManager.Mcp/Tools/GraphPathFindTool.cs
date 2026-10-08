using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphPathFindTool
{
    private readonly GraphRegistry _registry;

    public GraphPathFindTool(GraphRegistry registry)
    {
        _registry = registry;
    }

    [McpServerTool(Name = "graph_path_find"), Description("Find the directed shortest path between two nodes in the knowledge graph. Returns an ordered list of node IDs from source to target (inclusive). Returns an error if either node is not found or no directed path exists.")]
    public Task<string> GraphPathFindAsync(
        [Description("Absolute path to the .sln whose graph to query, the same path passed to project_scan. The graph is read from .context-manager/graph.json next to it and reloaded whenever that file changes.")] string solutionPath,
        [Description("The ISymbol.ToDisplayString() ID of the source node.")] string sourceId,
        [Description("The ISymbol.ToDisplayString() ID of the target node.")] string targetId,
        CancellationToken ct = default)
    {
        var store = _registry.TryLoad(solutionPath);
        if (store is null)
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("graph_not_found", $"No graph for {solutionPath}. Run project_scan first.", solutionPath),
                AnalysisJson.Options));

        if (!store.TryGetNode(sourceId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {sourceId}", sourceId),
                AnalysisJson.Options));

        if (!store.TryGetNode(targetId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {targetId}", targetId),
                AnalysisJson.Options));

        var path = store.ShortestPath(sourceId, targetId, ct);

        if (path.Count == 0)
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("no_path", "No directed path found between the two nodes.", null),
                AnalysisJson.Options));

        return Task.FromResult(JsonSerializer.Serialize(path, AnalysisJson.Options));
    }
}
