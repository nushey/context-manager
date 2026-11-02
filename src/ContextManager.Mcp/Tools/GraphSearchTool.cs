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

    [McpServerTool(Name = ToolName), Description("Discover files and methods by possible names when no file path or node ID is known. A term matches an ordinal case-insensitive substring of a file name without .cs or a method name without its signature; any term is enough. Constructors match their type name. Searches every Method node in the graph, including private methods; directory paths, parameter types, bodies and namespaces are not searched. Terms are trimmed before matching; an empty term list, or any term shorter than 3 characters after trimming, rejects the entire query with invalid_query before synchronization. Selects exact matches before partial matches, each in graph encounter order, without boosting names that match several terms, up to 50 unique file/method matches before grouping by file (one per matching file and one per matching method ID). Returns compact JSON with results and truncated; each result always has file, namespaces (every namespace in the file), fileMatched and methods, and each method has name and nodeId. Arrays remain present even when empty. fileMatched is true only when the file-name match was kept within the limit; a file can appear through a kept method while its own name match was omitted. truncated means additional matches were omitted; there is no pagination, so narrow the terms. Pass a returned file to inspect_file, or a nodeId unchanged to the other graph tools. " + GraphSyncRunner.QuerySynchronizationDescription)]
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
