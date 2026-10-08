using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class ProjectScanToolTests
{
    private static ProjectScanTool CreateTool()
    {
        var store = new GraphStore();
        var edgeExtractor = new EdgeExtractor();
        var builder = new GraphBuilder(store, edgeExtractor);
        return new ProjectScanTool(builder, store);
    }

    [TestMethod]
    public async Task ProjectScanAsync_NonSlnPath_ReturnsInvalidSolutionPathError()
    {
        var tool = CreateTool();

        var json = await tool.ProjectScanAsync("/some/path/project.csproj");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("invalid_solution_path", error!.Code);
    }

    [TestMethod]
    public async Task ProjectScanAsync_NonExistentSlnFile_ReturnsInvalidSolutionPathError()
    {
        var tool = CreateTool();
        var missing = "/nonexistent/path/Missing.sln";

        var json = await tool.ProjectScanAsync(missing);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("invalid_solution_path", error!.Code);
        Assert.AreEqual(missing, error.FilePath);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_ValidSolution_ReturnsSummaryAndWritesGraphJson()
    {
        var store = new GraphStore();
        var solutionPath = FindRepoSolutionPath();
        var graphJsonPath = Path.Combine(Path.GetTempPath(), $"context-manager-{Guid.NewGuid():N}.json");
        var tool = new ProjectScanTool(
            new GraphBuilder(store, new EdgeExtractor()),
            store,
            (path, json, token) => path.EndsWith("graph.json")
                ? File.WriteAllTextAsync(graphJsonPath, json, token)
                : Task.CompletedTask);

        var result = await tool.ProjectScanAsync(solutionPath);

        // Must match "Scan complete. N nodes, M edges." format.
        Assert.IsTrue(
            Regex.IsMatch(result, @"^Scan (complete|partial)\. \d+ nodes, \d+ edges\. Coverage:"),
            $"Unexpected result: {result}");

        // NodeCount must be > 0 (we scanned a real solution with C# files).
        Assert.IsTrue(
            ExtractNodeCount(result) > 0,
            $"Expected at least one node, got: {result}");

        // graph.json must have been written to disk.
        Assert.IsTrue(File.Exists(graphJsonPath), $"Expected graph.json at: {graphJsonPath}");

        // graph.json must not be empty.
        var written = await File.ReadAllTextAsync(graphJsonPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(written), "graph.json is empty.");
        File.Delete(graphJsonPath);
    }

    private static string FindRepoSolutionPath()
    {
        // Resolve solution path relative to this assembly's location, walking up to the repo root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var sln = directory.GetFiles("*.sln").FirstOrDefault();
            if (sln is not null)
                return sln.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate a .sln file from the test output directory.");
    }

    private static int ExtractNodeCount(string summary)
    {
        var match = Regex.Match(summary, @"(\d+) nodes");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    [TestMethod]
    public async Task ProjectScanAsync_PreCancelled_PreservesPublishedGraph()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("Previous", "Class"));
        var tool = new ProjectScanTool(new GraphBuilder(store, new EdgeExtractor()), store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var json = await tool.ProjectScanAsync(FindRepoSolutionPath(), cts.Token);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_cancelled", error?.Code);
        Assert.IsTrue(store.TryGetNode("Previous", out _));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_PersistenceFailure_PreservesPublishedGraph()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("Previous", "Class"));
        var builder = new GraphBuilder(store, new EdgeExtractor());
        var tool = new ProjectScanTool(builder, store, (_, _, _) => throw new IOException("controlled write failure"));

        var json = await tool.ProjectScanAsync(FindRepoSolutionPath());

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_failed", error?.Code);
        StringAssert.Contains(error?.Message, "controlled write failure");
        Assert.IsTrue(store.TryGetNode("Previous", out _));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_MixedLegacyAndMissingTargets_ReportsPartialCoverage()
    {
        var store = new GraphStore();
        var writes = new Dictionary<string, string>();
        var tool = new ProjectScanTool(
            new GraphBuilder(store, new EdgeExtractor()),
            store,
            (path, content, _) =>
            {
                writes[path] = content;
                return Task.CompletedTask;
            });
        var solutionPath = Path.Combine(FindRepoRoot(), "tests", "ContextManager.Analysis.Tests", "Fixtures", "ScanFixtures", "Coverage.sln");

        var result = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "unsupported projects");
        StringAssert.Contains(result, "Diagnostics (");
        StringAssert.Contains(result, "scan-diagnostics.log");
        var log = writes.Single(w => w.Key.EndsWith("scan-diagnostics.log"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(log.Value));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_DiagnosticsLogWriteFailure_KeepsScanResult()
    {
        var store = new GraphStore();
        var tool = new ProjectScanTool(
            new GraphBuilder(store, new EdgeExtractor()),
            store,
            (path, _, _) => path.EndsWith("scan-diagnostics.log")
                ? throw new IOException("log write failure")
                : Task.CompletedTask);
        var solutionPath = Path.Combine(FindRepoRoot(), "tests", "ContextManager.Analysis.Tests", "Fixtures", "ScanFixtures", "Coverage.sln");

        var result = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "Diagnostics log not written: log write failure");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_EmptyScope_ReturnsDedicatedErrorAndPreservesSnapshot()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("Previous", "Class"));
        var writes = new Dictionary<string, string>();
        var tool = new ProjectScanTool(
            new GraphBuilder(store, new EdgeExtractor()),
            store,
            (path, content, _) =>
            {
                writes[path] = content;
                return Task.CompletedTask;
            });
        var solutionPath = Path.Combine(FindRepoRoot(), "tests", "ContextManager.Analysis.Tests", "Fixtures", "ScanFixtures", "Empty.sln");

        var json = await tool.ProjectScanAsync(solutionPath);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_empty", error?.Code);
        Assert.IsTrue(store.TryGetNode("Previous", out _));
        var log = writes.Single(w => w.Key.EndsWith("scan-diagnostics.log"));
        Assert.AreEqual(string.Empty, log.Value, error?.Message);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_CancelledDuringDiagnosticsLogWrite_KeepsNewGraphAndReportsLogFailure()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("Previous", "Class"));
        using var cts = new CancellationTokenSource();
        var tool = new ProjectScanTool(
            new GraphBuilder(store, new EdgeExtractor()),
            store,
            (path, _, token) =>
            {
                if (!path.EndsWith("scan-diagnostics.log"))
                    return Task.CompletedTask;
                cts.Cancel();
                throw new OperationCanceledException(token);
            });
        var solutionPath = Path.Combine(FindRepoRoot(), "tests", "ContextManager.Analysis.Tests", "Fixtures", "ScanFixtures", "Coverage.sln");

        var result = await tool.ProjectScanAsync(solutionPath, cts.Token);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "Diagnostics log not written:");
        Assert.IsFalse(store.TryGetNode("Previous", out _));
        Assert.IsTrue(store.TryGetNode("ScanFixtures.SdkType", out _));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    [TestMethod]
    public async Task AtomicWriter_ReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"context-manager-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "graph.json");
        await File.WriteAllTextAsync(target, "old");
        var method = typeof(ProjectScanTool).GetMethod("WriteAtomicallyAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var task = (Task?)method.Invoke(null, [target, "new", CancellationToken.None]);
        Assert.IsNotNull(task);
        await task;

        Assert.AreEqual("new", await File.ReadAllTextAsync(target));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public void FormatDiagnostics_GroupsOrdersTruncatesAndBounds()
    {
        var method = typeof(ProjectScanTool).GetMethod("FormatDiagnostics", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        const string logPath = "/repo/.context-manager/scan-diagnostics.log";
        string Format(IReadOnlyList<GraphBuildDiagnostic> diagnostics) =>
            (string)method.Invoke(null, [diagnostics, logPath])!;

        var grouped = Format(
        [
            new GraphBuildDiagnostic("Warning", "A"),
            new GraphBuildDiagnostic("Warning", "B"),
            new GraphBuildDiagnostic("Warning", "A"),
            new GraphBuildDiagnostic("Warning", "B"),
            new GraphBuildDiagnostic("Warning", "C"),
            new GraphBuildDiagnostic("Warning", "C"),
            new GraphBuildDiagnostic("Warning", "C"),
            new GraphBuildDiagnostic("Failure", "F"),
        ]);
        Assert.AreEqual(
            $" Diagnostics (4 groups, 8 total): Failure (x1): F | Warning (x3): C | Warning (x2): A | Warning (x2): B | full log: {logPath}",
            grouped);

        var many = Format(Enumerable.Range(0, 12).Select(i => new GraphBuildDiagnostic("Warning", $"M{i}")).ToList());
        var entries = many.Split(" | ");
        Assert.AreEqual(11, entries.Length);
        Assert.AreEqual($"+2 more groups, see {logPath}", entries[^1]);
        StringAssert.Contains(entries[9], "Warning (x1): M9");

        var truncated = Format([new GraphBuildDiagnostic("Failure", new string('x', 500))]);
        var message = truncated.Split("Failure (x1): ")[1].Split(" | ")[0];
        Assert.AreEqual(300, message.Length);
        Assert.IsTrue(message.EndsWith('…'));

        Assert.AreEqual(string.Empty, Format([]));
    }
}
