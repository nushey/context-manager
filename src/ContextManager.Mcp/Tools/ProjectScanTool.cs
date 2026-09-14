using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectScanTool
{
    private readonly GraphBuilder _builder;
    private readonly Func<string, string, CancellationToken, Task> _writeGraph;

    public ProjectScanTool(GraphBuilder builder, GraphStore store)
        : this(builder, store, WriteAtomicallyAsync)
    {
    }

    public ProjectScanTool(
        GraphBuilder builder,
        GraphStore store,
        Func<string, string, CancellationToken, Task> writeGraph)
    {
        _builder = builder;
        _writeGraph = writeGraph;
    }

    [McpServerTool(Name = "project_scan"), Description(
        "Scan a .NET solution and build a knowledge graph from loaded C# source documents. " +
        "The result reports project/document coverage, unsupported languages, skipped documents, and workspace diagnostics. " +
        "Persistence and in-memory publication succeed together; failed, empty, or cancelled scans preserve the previous graph.")]
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

        var phase = "msbuild_registration";
        try
        {
            MsBuildBootstrap.EnsureRegistered();
            Console.Error.WriteLine($"project_scan phase={phase} {MsBuildBootstrap.DescribeRegistration()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"project_scan phase={phase} failed:{Environment.NewLine}{ex}");
            return JsonSerializer.Serialize(
                new AnalysisError("msbuild_not_found",
                    $"MSBuild could not be located on this machine. Install Visual Studio or the Build Tools (.NET desktop build tools workload). Underlying error: {FormatExceptionChain(ex)}",
                    null),
                AnalysisJson.Options);
        }

        try
        {
            var dir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
            var outputDir = Path.Combine(dir, ".context-manager");
            var graphJsonPath = Path.Combine(outputDir, "graph.json");
            phase = "solution_evaluation_and_extraction";
            var result = await _builder.BuildWithReportAsync(
                solutionPath,
                (json, token) =>
                {
                    phase = "persistence";
                    return _writeGraph(graphJsonPath, json, token);
                },
                ct);

            var coverage = $"{result.LoadedProjects}/{result.TotalProjects} projects, " +
                           $"{result.LoadedDocuments}/{result.TotalDocuments} documents, " +
                           $"{result.UnsupportedProjects} unsupported projects, " +
                           $"{result.SkippedProjects} skipped projects, " +
                           $"{result.SkippedDocuments} skipped documents";
            var diagnosticText = result.Diagnostics.Count == 0
                ? string.Empty
                : $" Diagnostics: {string.Join(" | ", result.Diagnostics.Select(d => $"{d.Kind}: {d.Message}"))}";

            if (result.Status is "failed" or "empty")
            {
                var code = result.Status == "empty" ? "scan_empty" : "scan_incomplete";
                var message = result.Status == "empty"
                    ? $"The loaded C# scope contained no graphable type declarations. Coverage: {coverage}.{diagnosticText}"
                    : $"No usable C# scope was loaded. Coverage: {coverage}.{diagnosticText}";
                return JsonSerializer.Serialize(
                    new AnalysisError(code, message, solutionPath),
                    AnalysisJson.Options);
            }

            return $"Scan {result.Status}. {result.NodeCount} nodes, {result.EdgeCount} edges. Coverage: {coverage}.{diagnosticText}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"project_scan phase={phase} cancelled. {MsBuildBootstrap.DescribeRegistration()}");
            return JsonSerializer.Serialize(
                new AnalysisError("scan_cancelled", "The solution scan was cancelled; the previous graph remains active.", solutionPath),
                AnalysisJson.Options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"project_scan phase={phase} failed. {MsBuildBootstrap.DescribeRegistration()}{Environment.NewLine}{ex}");
            return JsonSerializer.Serialize(
                new AnalysisError("scan_failed", $"phase={phase}; {FormatExceptionChain(ex)}", solutionPath),
                AnalysisJson.Options);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, ct);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string FormatExceptionChain(Exception ex)
    {
        var chain = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            chain.Add($"{current.GetType().FullName}: {current.Message}");

        return string.Join(" --> ", chain);
    }
}
