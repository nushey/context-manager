using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;

namespace ContextManager.Mcp.Tools;

internal static class GraphSyncRunner
{
    public const string QuerySynchronizationDescription = "Before answering, the graph is synchronized: built when graph.json is missing, otherwise only the invalidated contributions are re-extracted; source and MSBuild input files are compared by content hash, while changes that only alter environment-dependent MSBuild evaluation are not detected (run project_scan to force re-evaluation); a failed synchronization returns the same error as project_scan and keeps the previous graph.";

    public const string QuerySolutionPathDescription = "Absolute path to the .sln whose graph to query, the same path passed to project_scan. The graph lives in .context-manager/graph.json next to it; before the query it is built when missing, or brought up to date when sources, evaluated document membership, or MSBuild inputs changed.";

    private const int MaxInlineDiagnosticGroups = 10;
    private const int MaxInlineDiagnosticLength = 300;

    internal sealed record Outcome(GraphSyncResult? Result, string? ErrorJson, string Coverage, string DiagnosticText);

    public static void EnsureMsBuildRegistered()
    {
        MsBuildBootstrap.EnsureRegistered();
        Console.Error.WriteLine($"graph_sync phase=msbuild_registration {MsBuildBootstrap.DescribeRegistration()}");
    }

    public static async Task<Outcome> RunAsync(
        IGraphSynchronizer synchronizer,
        string solutionPath,
        bool reevaluate,
        string tool,
        Func<string, string, CancellationToken, Task> writeFile,
        CancellationToken ct)
    {
        GraphSyncResult? result;
        try
        {
            result = await synchronizer.SynchronizeAsync(solutionPath, reevaluate, ct);
        }
        catch (GraphSyncException ex) when (ex.Phase == "msbuild_registration")
        {
            Console.Error.WriteLine($"{tool} phase={ex.Phase} failed:{Environment.NewLine}{ex.InnerException}");
            return Failure(new AnalysisError("msbuild_not_found",
                $"MSBuild could not be located on this machine. Install Visual Studio or the Build Tools (.NET desktop build tools workload). Underlying error: {FormatExceptionChain(ex.InnerException!)}",
                null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{tool} cancelled. {MsBuildBootstrap.DescribeRegistration()}");
            return Failure(new AnalysisError("scan_cancelled", "The solution scan was cancelled; the previous graph remains active.", solutionPath));
        }
        catch (GraphSyncException ex)
        {
            Console.Error.WriteLine($"{tool} phase={ex.Phase} failed. {MsBuildBootstrap.DescribeRegistration()}{Environment.NewLine}{ex.InnerException}");
            return Failure(new AnalysisError("scan_failed", $"phase={ex.Phase}; {FormatExceptionChain(ex.InnerException!)}", solutionPath));
        }

        if (result is null)
            return new Outcome(null, null, string.Empty, string.Empty);

        var t = result.Timings;
        Console.Error.WriteLine(
            $"{tool} graph_sync changed={result.Changed} extracted={result.ExtractedDocuments.Count} " +
            $"freshness={t.Freshness.TotalMilliseconds:F0}ms evaluation={t.Evaluation.TotalMilliseconds:F0}ms " +
            $"compilation={t.Compilation.TotalMilliseconds:F0}ms extraction={t.Extraction.TotalMilliseconds:F0}ms " +
            $"index={t.IndexUpdate.TotalMilliseconds:F0}ms persistence={t.Persistence.TotalMilliseconds:F0}ms");

        var report = result.Report;
        var failed = report.Status is "failed" or "empty";
        var diagnosticsLogPath = Path.Combine(Path.GetDirectoryName(GraphRegistry.GetGraphPath(solutionPath))!, "scan-diagnostics.log");
        var logFailureText = string.Empty;
        if (reevaluate || result.Changed || failed)
        {
            try
            {
                await writeFile(
                    diagnosticsLogPath,
                    string.Join(Environment.NewLine, report.Diagnostics.Select(d => $"{d.Kind}: {d.Message}")),
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{tool} phase=diagnostics_log failed: {ex}");
                logFailureText = $" Diagnostics log not written: {ex.Message}";
            }
        }

        var coverage = $"{report.LoadedProjects}/{report.TotalProjects} projects, " +
                       $"{report.LoadedDocuments}/{report.TotalDocuments} documents, " +
                       $"{report.UnsupportedProjects} unsupported projects, " +
                       $"{report.SkippedProjects} skipped projects, " +
                       $"{report.SkippedDocuments} skipped documents";
        var diagnosticText = FormatDiagnostics(report.Diagnostics, diagnosticsLogPath) + logFailureText;

        if (!failed)
            return new Outcome(result, null, coverage, diagnosticText);

        var code = report.Status == "empty" ? "scan_empty" : "scan_incomplete";
        var message = report.Status == "empty"
            ? $"The loaded C# scope contained no graphable type declarations. Coverage: {coverage}.{diagnosticText}"
            : $"No usable C# scope was loaded. Coverage: {coverage}.{diagnosticText}";
        return new Outcome(result, Serialize(new AnalysisError(code, message, solutionPath)), coverage, diagnosticText);
    }

    public static async Task<(GraphStore? Graph, string? ErrorJson)> LoadForQueryAsync(
        GraphRegistry registry,
        IGraphSynchronizer? synchronizer,
        string solutionPath,
        string tool,
        CancellationToken ct)
    {
        if (synchronizer is null)
        {
            var loaded = registry.TryLoad(solutionPath)?.Capture();
            return loaded is null
                ? (null, Serialize(new AnalysisError("graph_not_found", $"No graph for {solutionPath}. Run project_scan first.", solutionPath)))
                : (loaded, null);
        }

        var outcome = await RunAsync(synchronizer, solutionPath, reevaluate: false, tool, GraphRegistry.WriteAtomicallyAsync, ct);
        if (outcome.ErrorJson is not null)
            return (null, outcome.ErrorJson);

        return outcome.Result is null
            ? (null, Serialize(new AnalysisError("graph_not_found", $"No graph and no solution file found for {solutionPath}.", solutionPath)))
            : (outcome.Result.Graph, null);
    }

    public static string Serialize(AnalysisError error) => JsonSerializer.Serialize(error, AnalysisJson.Options);

    private static Outcome Failure(AnalysisError error) => new(null, Serialize(error), string.Empty, string.Empty);

    private static string FormatDiagnostics(IReadOnlyList<GraphBuildDiagnostic> diagnostics, string logPath)
    {
        if (diagnostics.Count == 0)
            return string.Empty;

        var groups = diagnostics
            .GroupBy(d => (d.Kind, d.Message))
            .OrderByDescending(g => g.Key.Kind == WorkspaceDiagnosticKind.Failure.ToString())
            .ThenByDescending(g => g.Count())
            .ToList();

        var entries = groups
            .Take(MaxInlineDiagnosticGroups)
            .Select(g => $"{g.Key.Kind} (x{g.Count()}): {Truncate(g.Key.Message)}")
            .ToList();

        var omitted = groups.Count - entries.Count;
        entries.Add(omitted > 0 ? $"+{omitted} more groups, see {logPath}" : $"full log: {logPath}");

        return $" Diagnostics ({groups.Count} groups, {diagnostics.Count} total): {string.Join(" | ", entries)}";
    }

    private static string Truncate(string message) =>
        message.Length <= MaxInlineDiagnosticLength
            ? message
            : $"{message[..(MaxInlineDiagnosticLength - 1)]}…";

    private static string FormatExceptionChain(Exception ex)
    {
        var chain = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            chain.Add($"{current.GetType().FullName}: {current.Message}");

        return string.Join(" --> ", chain);
    }
}
