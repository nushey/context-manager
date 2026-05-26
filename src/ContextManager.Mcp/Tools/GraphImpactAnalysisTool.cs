using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphImpactAnalysisTool
{
    private static readonly IReadOnlySet<string> BackwardEdgeTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CALLS", "INJECTS" };

    private readonly GraphStore _store;

    public GraphImpactAnalysisTool(GraphStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "graph_impact_analysis"), Description("Perform a backward BFS from a node following CALLS and INJECTS edges. Returns the ordered list of affected node IDs — those that directly or transitively depend on the given node.")]
    public Task<string> GraphImpactAnalysisAsync(
        [Description("The ISymbol.ToDisplayString() ID of the node to analyze for impact.")] string nodeId,
        CancellationToken ct = default)
    {
        if (!_store.TryGetNode(nodeId, out _))
            return Task.FromResult(JsonSerializer.Serialize(
                new AnalysisError("node_not_found", $"Node not found in graph: {nodeId}", nodeId),
                AnalysisJson.Options));

        var affectedIds = _store.BfsBackward(nodeId, BackwardEdgeTypes);
        return Task.FromResult(JsonSerializer.Serialize(affectedIds, AnalysisJson.Options));
    }
}
