using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class GraphSearchTool(IGraphSynchronizer synchronizer)
{
    private const string ToolName = "graph_search";
    private const int MinimumTermLength = 3;

    [McpServerTool(Name = ToolName), Description("Discover files and methods by name using any query term, with ordinal case-insensitive substring matching. File names exclude .cs; method names exclude signatures, and constructors match their type name. Includes every Method node in the graph, including private methods. Selects exact matches before partial matches, preserving graph encounter order, up to 50 unique file/method matches before grouping by file. Returns compact JSON with results and truncated; each result always has file, namespaces, fileMatched and methods, each method has name and nodeId. Arrays remain present even when empty. fileMatched reflects a selected file match. truncated means additional matches were omitted; there is no pagination. An empty term list, or any term with fewer than 3 characters after trimming whitespace, rejects the entire query with invalid_query before synchronization. Terms are trimmed before matching. " + GraphSyncRunner.QuerySynchronizationDescription)]
    public async Task<string> GraphSearchAsync(
        [Description(GraphSyncRunner.QuerySolutionPathDescription)] string solutionPath,
        [Description("Possible file or method names, at least one; any term can match. Each term must be at least 3 characters long after trimming whitespace.")] IReadOnlyList<string> queries,
        CancellationToken ct = default)
    {
        var terms = queries.Select(q => q?.Trim() ?? string.Empty).ToList();
        if (terms.Count == 0 || terms.Any(t => t.Length < MinimumTermLength))
            return JsonSerializer.Serialize(
                new AnalysisError("invalid_query", $"Provide at least one query term; each term must be at least {MinimumTermLength} characters long after trimming whitespace.", null),
                AnalysisJson.Options);

        var (store, error) = await GraphSyncRunner.LoadForQueryAsync(synchronizer, solutionPath, ToolName, ct);
        if (store is null)
            return error!;

        var result = store.Search(terms, ct);
        var contract = new GraphSearchResultContract(result.Results.Select(f => new GraphSearchFileContract(
            f.File, f.Namespaces, f.FileMatched,
            f.Methods.Select(m => new GraphSearchMethodContract(m.Name, m.NodeId)).ToList())).ToList(), result.Truncated);
        return JsonSerializer.Serialize(contract, AnalysisJson.Options);
    }
}
