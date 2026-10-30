using System.ComponentModel;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectScanTool
{
    private readonly IGraphSynchronizer _synchronizer;
    private readonly Func<string, string, CancellationToken, Task> _writeFile;

    [ActivatorUtilitiesConstructor]
    public ProjectScanTool(IGraphSynchronizer synchronizer)
        : this(synchronizer, GraphRegistry.WriteAtomicallyAsync)
    {
    }

    public ProjectScanTool(IGraphSynchronizer synchronizer, Func<string, string, CancellationToken, Task> writeFile)
    {
        _synchronizer = synchronizer;
        _writeFile = writeFile;
    }

    [McpServerTool(Name = "project_scan"), Description(
        "Scan a .NET solution and bring its knowledge graph up to date from loaded C# source documents. " +
        "The first scan builds the graph; later scans re-evaluate the solution with MSBuild and re-extract only the documents of projects whose sources, " +
        "evaluated membership, or compilation inputs changed (plus the projects that depend on them), reusing every other contribution. " +
        "The result reports project/document coverage, unsupported languages, skipped documents, and a bounded summary of workspace diagnostics: " +
        "identical diagnostics are grouped with their counts, failures first, at most 10 groups inline with long messages truncated. " +
        "The full raw diagnostic list is written to .context-manager/scan-diagnostics.log next to graph.json on every scan. " +
        "Persistence and in-memory publication succeed together; failed, empty, or cancelled scans preserve the previous graph. " +
        "The graph query tools read graph.json by this solutionPath, so every session working on the solution sees the latest scan.")]
    public async Task<string> ProjectScanAsync(
        [Description("Absolute path to a .sln file to scan.")] string solutionPath,
        CancellationToken ct = default)
    {
        if (!solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return GraphSyncRunner.Serialize(new AnalysisError("invalid_solution_path", "Path must end with .sln.", solutionPath));

        if (!File.Exists(solutionPath))
            return GraphSyncRunner.Serialize(new AnalysisError("invalid_solution_path", $"Solution file not found: {solutionPath}", solutionPath));

        var outcome = await GraphSyncRunner.RunAsync(_synchronizer, solutionPath, reevaluate: true, "project_scan", _writeFile, ct);
        if (outcome.ErrorJson is not null)
            return outcome.ErrorJson;

        if (outcome.Result is null)
            return GraphSyncRunner.Serialize(new AnalysisError("invalid_solution_path", $"Solution file not found: {solutionPath}", solutionPath));

        var report = outcome.Result.Report;
        return $"Scan {report.Status}. {report.NodeCount} nodes, {report.EdgeCount} edges. Coverage: {outcome.Coverage}.{outcome.DiagnosticText}";
    }
}
