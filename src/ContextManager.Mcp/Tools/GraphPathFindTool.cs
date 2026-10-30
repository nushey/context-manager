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
    private readonly IGraphSynchronizer _synchronizer;

    public GraphPathFindTool(IGraphSynchronizer synchronizer)
    {
        _synchronizer = synchronizer;
    }

    [McpServerTool(Name = "graph_path_find"), Description("Find the directed shortest path between two nodes in the knowledge graph. " + GraphSyncRunner.QuerySynchronizationDescription + " Returns an ordered list of node IDs from source to target (inclusive). Returns an error if either node is not found or no directed path exists.")]
    public async Task<string> GraphPathFindAsync(
        [Description(GraphSyncRunner.QuerySolutionPathDescription)] string solutionPath,
        [Description("The ISymbol.ToDisplayString() ID of the source node.")] string sourceId,
        [Description("The ISymbol.ToDisplayString() ID of the target node.")] string targetId,
        CancellationToken ct = default)
    {
        var (store, error) = await GraphSyncRunner.LoadForQueryAsync(_synchronizer, solutionPath, "graph_path_find", ct);
        if (store is null)
            return error!;

        if (!store.TryGetNode(sourceId, out _))
            return JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {sourceId}", sourceId),
                AnalysisJson.Options);

        if (!store.TryGetNode(targetId, out _))
            return JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {targetId}", targetId),
                AnalysisJson.Options);

        var path = store.ShortestPath(sourceId, targetId, ct);

        if (path.Count == 0)
            return JsonSerializer.Serialize(
                new AnalysisError("no_path", "No directed path found between the two nodes.", null),
                AnalysisJson.Options);

        return JsonSerializer.Serialize(path, AnalysisJson.Options);
    }
}
