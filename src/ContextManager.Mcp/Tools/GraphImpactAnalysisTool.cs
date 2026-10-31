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
        new HashSet<string>(StringComparer.Ordinal) { "CALLS", "INJECTS", "REFERENCES", "RETURNS", "INHERITS" };

    private readonly IGraphSynchronizer _synchronizer;

    public GraphImpactAnalysisTool(IGraphSynchronizer synchronizer)
    {
        _synchronizer = synchronizer;
    }

    [McpServerTool(Name = "graph_impact_analysis"), Description(
        "Performs an impact-specific backward traversal from a node following CALLS, INJECTS, REFERENCES, RETURNS, and INHERITS edges. " +
        GraphSyncRunner.QuerySynchronizationDescription + " " +
        "Seeds the BFS with the node, its direct members (via CONTAINS), and any interfaces it implements (via IMPLEMENTS) plus their members, " +
        "so member-level dependencies aggregate to their declaring type. " +
        "The implementors of every interface reached are included. " +
        "Member nodes reached during traversal are rolled up to their declaring type. " +
        "Returns an object with 'affectedIds' (ordered list of impacted type IDs) and 'affectedFiles' (each returned affected ID mapped to its declaring source file, when known, ready for inspect_file).")]
    public async Task<string> GraphImpactAnalysisAsync(
        [Description(GraphSyncRunner.QuerySolutionPathDescription)] string solutionPath,
        [Description("The ISymbol.ToDisplayString() ID of the node to analyze for impact.")] string nodeId,
        CancellationToken ct = default)
    {
        var (store, error) = await GraphSyncRunner.LoadForQueryAsync(_synchronizer, solutionPath, "graph_impact_analysis", ct);
        if (store is null)
            return error!;

        if (!store.TryGetNode(nodeId, out _))
            return JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", null),
                AnalysisJson.Options);

        var affectedIds = store.ImpactBackward(nodeId, BackwardEdgeTypes, ct);
        var affectedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in affectedIds)
        {
            if (store.TryGetNode(id, out var node) && node!.File is not null)
                affectedFiles[id] = node.File;
        }

        var result = new GraphImpactResult(affectedIds, affectedFiles.Count == 0 ? null : affectedFiles);

        return JsonSerializer.Serialize(result, AnalysisJson.Options);
    }
}
