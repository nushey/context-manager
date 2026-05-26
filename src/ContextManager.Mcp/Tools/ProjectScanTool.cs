using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectScanTool
{
    private readonly GraphBuilder _builder;
    private readonly GraphStore _store;

    public ProjectScanTool(GraphBuilder builder, GraphStore store)
    {
        _builder = builder;
        _store = store;
    }

    [McpServerTool(Name = "project_scan"), Description("Scan a .NET solution and build an in-memory knowledge graph from all C# source files. The in-memory graph is replaced on each scan — subsequent calls overwrite the previous graph.")]
    public async Task<string> ProjectScanAsync(
        [Description("Absolute path to a .sln file to scan.")] string solutionPath,
        CancellationToken ct = default)
    {
        if (!solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(
                new AnalysisError("invalid_solution_path", "Path must end with .sln.", solutionPath),
                AnalysisJson.Options);

        if (!File.Exists(solutionPath))
            return JsonSerializer.Serialize(
                new AnalysisError("invalid_solution_path", $"Solution file not found: {solutionPath}", solutionPath),
                AnalysisJson.Options);

        try
        {
            await _builder.BuildAsync(solutionPath, ct);

            var outputDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, ".context-manager");
            Directory.CreateDirectory(outputDir);

            var graphJsonPath = Path.Combine(outputDir, "graph.json");
            await File.WriteAllTextAsync(graphJsonPath, _store.Serialize(), ct);

            return $"Scan complete. {_store.NodeCount} nodes, {_store.EdgeCount} edges.";
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new AnalysisError("scan_failed", ex.Message, solutionPath),
                AnalysisJson.Options);
        }
    }
}
