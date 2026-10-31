using System.ComponentModel;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectScanTool
{
    private readonly IGraphSynchronizer _synchronizer;

    public ProjectScanTool(IGraphSynchronizer synchronizer)
    {
        _synchronizer = synchronizer;
    }

    [McpServerTool(Name = "project_scan"), Description(
        "Scan a .NET solution and bring its knowledge graph up to date from loaded C# source documents. " +
        "The first scan builds the graph; later scans re-evaluate the solution with MSBuild and re-extract only the documents of projects whose sources, " +
        "evaluated membership, or compilation inputs changed (plus the projects that depend on them), reusing every other contribution. " +
        "The result reports project/document coverage, unsupported languages, and skipped documents. " +
        "Persistence and in-memory publication succeed together; failed, empty, or cancelled scans preserve the previous graph. " +
        "The graph is persisted in .context-manager/<solution name>.graph.json next to the .sln, so every session working on the solution sees the latest scan.")]
    public async Task<string> ProjectScanAsync(
        [Description("Absolute path to a .sln file to scan.")] string solutionPath,
        CancellationToken ct = default)
    {
        if (!solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return GraphSyncRunner.Serialize(new AnalysisError("invalid_solution_path", "Path must end with .sln.", solutionPath));

        if (!File.Exists(solutionPath))
            return GraphSyncRunner.Serialize(new AnalysisError("invalid_solution_path", $"Solution file not found: {solutionPath}", solutionPath));

        var outcome = await GraphSyncRunner.RunAsync(_synchronizer, solutionPath, reevaluate: true, "project_scan", ct);
        if (outcome.ErrorJson is not null)
            return outcome.ErrorJson;

        var report = outcome.Result!.Report;
        return $"Scan {report.Status}. {report.NodeCount} nodes, {report.EdgeCount} edges. Coverage: {outcome.Coverage}.";
    }
}
