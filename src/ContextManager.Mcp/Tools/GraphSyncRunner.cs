using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;

namespace ContextManager.Mcp.Tools;

internal static class GraphSyncRunner
{
    public const string QuerySynchronizationDescription = "Before answering, the graph is synchronized: built when graph.json is missing, otherwise only the invalidated contributions are re-extracted; source and MSBuild input files are compared by content hash, while changes that only alter environment-dependent MSBuild evaluation are not detected (run project_scan to force re-evaluation); a failed synchronization returns the same error as project_scan and keeps the previous graph.";

    public const string QuerySolutionPathDescription = "Absolute path to the .sln whose graph to query, the same path passed to project_scan. The graph lives in .context-manager/graph.json next to it; before the query it is built when missing, or brought up to date when sources, evaluated document membership, or MSBuild inputs changed.";

    internal sealed record Outcome(GraphSyncResult? Result, string? ErrorJson, string Coverage);

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
            return new Outcome(null, null, string.Empty);

        var report = result.Report;
        var failed = report.Status is "failed" or "empty";
        var coverage = $"{report.LoadedProjects}/{report.TotalProjects} projects, " +
                       $"{report.LoadedDocuments}/{report.TotalDocuments} documents, " +
                       $"{report.UnsupportedProjects} unsupported projects, " +
                       $"{report.SkippedProjects} skipped projects, " +
                       $"{report.SkippedDocuments} skipped documents";
        if (!failed)
            return new Outcome(result, null, coverage);

        var code = report.Status == "empty" ? "scan_empty" : "scan_incomplete";
        var message = report.Status == "empty"
            ? $"The loaded C# scope contained no graphable type declarations. Coverage: {coverage}."
            : $"No usable C# scope was loaded. Coverage: {coverage}.";
        return new Outcome(result, Serialize(new AnalysisError(code, message, solutionPath)), coverage);
    }

    public static async Task<(GraphStore? Graph, string? ErrorJson)> LoadForQueryAsync(
        IGraphSynchronizer synchronizer,
        string solutionPath,
        string tool,
        CancellationToken ct)
    {
        var outcome = await RunAsync(synchronizer, solutionPath, reevaluate: false, tool, ct);
        if (outcome.ErrorJson is not null)
            return (null, outcome.ErrorJson);

        return outcome.Result is null
            ? (null, Serialize(new AnalysisError("graph_not_found", $"No graph and no solution file found for {solutionPath}.", solutionPath)))
            : (outcome.Result.Graph, null);
    }

    public static string Serialize(AnalysisError error) => JsonSerializer.Serialize(error, AnalysisJson.Options);

    private static Outcome Failure(AnalysisError error) => new(null, Serialize(error), string.Empty);

    private static string FormatExceptionChain(Exception ex)
    {
        var chain = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            chain.Add($"{current.GetType().FullName}: {current.Message}");

        return string.Join(" --> ", chain);
    }
}
