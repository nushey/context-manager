using System.Text.Json;
using System.Reflection;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class ProjectScanToolTests : FixtureTest
{
    private ProjectScanTool CreateTool(GraphRegistry? registry = null, Func<string, string, CancellationToken, Task>? writeFile = null)
    {
        writeFile ??= GraphRegistry.WriteAtomicallyAsync;
        return new ProjectScanTool(CreateSynchronizer(registry ?? new GraphRegistry(), writeFile), writeFile);
    }

    private string CopyCoverageSolution() => Path.Combine(CopyFixture("ScanFixtures"), "Coverage.sln");

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

    private string CopyIncrementalSolution() => Path.Combine(CopyFixture("GraphFixtures", "IncrementalFixtures"), "Incremental.sln");

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_ValidSolution_ReturnsSummaryAndWritesGraphJson()
    {
        var solutionPath = CopyIncrementalSolution();

        var result = await CreateTool().ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(result, "Scan partial. 12 nodes, 13 edges. Coverage:");
        var persisted = new GraphStore();
        persisted.Deserialize(await File.ReadAllTextAsync(GraphRegistry.GetGraphPath(solutionPath)));
        Assert.AreEqual(12, persisted.NodeCount);
        Assert.AreEqual(13, persisted.EdgeCount);
        Assert.IsTrue(persisted.TryGetNode("IncrementalFixtures.Consumer.Client", out _));
    }

    [TestMethod]
    public async Task ProjectScanAsync_PreCancelled_PreservesPublishedGraph()
    {
        var solutionPath = CopyIncrementalSolution();
        var registry = new GraphRegistry();
        var store = registry.GetStore(solutionPath);
        store.AddNode(new GraphNode("Previous", "Class"));
        var tool = CreateTool(registry);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var json = await tool.ProjectScanAsync(solutionPath, cts.Token);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_cancelled", error?.Code);
        Assert.IsTrue(store.TryGetNode("Previous", out _));
        Assert.IsFalse(File.Exists(GraphRegistry.GetGraphPath(solutionPath)));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_PersistenceFailure_PreservesPublishedGraph()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var registry = new GraphRegistry();
        var store = registry.GetStore(solutionPath);
        store.AddNode(new GraphNode("Previous", "Class"));
        var tool = CreateTool(registry, (_, _, _) => throw new IOException("controlled write failure"));

        var json = await tool.ProjectScanAsync(solutionPath);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_failed", error?.Code);
        StringAssert.Contains(error?.Message, "phase=persistence");
        StringAssert.Contains(error?.Message, "controlled write failure");
        Assert.IsTrue(store.TryGetNode("Previous", out _));
        Assert.IsFalse(File.Exists(GraphRegistry.GetGraphPath(solutionPath)));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_MixedLegacyAndMissingTargets_ReportsPartialCoverage()
    {
        var writes = new Dictionary<string, string>();
        var tool = CreateTool(writeFile: (path, content, _) =>
        {
            writes[path] = content;
            return Task.CompletedTask;
        });
        var solutionPath = CopyCoverageSolution();

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
        var tool = CreateTool(writeFile: (path, _, _) => path.EndsWith("scan-diagnostics.log")
            ? throw new IOException("log write failure")
            : Task.CompletedTask);
        var solutionPath = CopyCoverageSolution();

        var result = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "Diagnostics log not written: log write failure");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_EmptyScope_ReturnsDedicatedErrorAndPreservesSnapshot()
    {
        var directory = CopyFixture("ScanFixtures");
        var solutionPath = Path.Combine(directory, "Empty.sln");
        var registry = new GraphRegistry();
        var store = registry.GetStore(solutionPath);
        store.AddNode(new GraphNode("Previous", "Class"));
        var writes = new Dictionary<string, string>();
        var tool = CreateTool(registry, (path, content, _) =>
        {
            writes[path] = content;
            return Task.CompletedTask;
        });

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
        var solutionPath = CopyCoverageSolution();
        var registry = new GraphRegistry();
        var store = registry.GetStore(solutionPath);
        store.AddNode(new GraphNode("Previous", "Class"));
        using var cts = new CancellationTokenSource();
        var tool = CreateTool(registry, (path, _, token) =>
        {
            if (!path.EndsWith("scan-diagnostics.log"))
                return Task.CompletedTask;
            cts.Cancel();
                throw new OperationCanceledException(token);
            });

        var result = await tool.ProjectScanAsync(solutionPath, cts.Token);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "Diagnostics log not written:");
        Assert.IsFalse(store.TryGetNode("Previous", out _));
        Assert.IsTrue(store.TryGetNode("ScanFixtures.SdkType", out _));
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ProjectScanAsync_SecondScanAfterEdit_UpdatesGraphIncrementally()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var registry = new GraphRegistry();
        var tool = CreateTool(registry);

        StringAssert.StartsWith(await tool.ProjectScanAsync(solutionPath), "Scan partial. 12 nodes, 13 edges.");
        var firstRevision = registry.TryLoad(solutionPath)!.Revision;
        var sender = Path.Combine(directory, "Core", "Sender.cs");
        FixtureCopy.Edit(sender, "        Audit.Record();\n", "");

        var second = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(second, "Scan partial. 12 nodes, 12 edges.");
        var graph = registry.TryLoad(solutionPath)!;
        Assert.AreNotEqual(firstRevision, graph.Revision);
        Assert.IsTrue(graph.HasProvenance);
        Assert.IsFalse(graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Sender").Any(n => n.Id == "IncrementalFixtures.Core.Audit"));
        Assert.IsTrue(File.Exists(Path.Combine(directory, ".context-manager", "scan-diagnostics.log")));
    }

    [TestMethod]
    public async Task AtomicWriter_ReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        var directory = NewTempDirectory();
        var target = Path.Combine(directory, "graph.json");
        await File.WriteAllTextAsync(target, "old");

        await GraphRegistry.WriteAtomicallyAsync(target, "new", CancellationToken.None);

        Assert.AreEqual("new", await File.ReadAllTextAsync(target));
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public void FormatDiagnostics_GroupsOrdersTruncatesAndBounds()
    {
        var method = typeof(ProjectScanTool).Assembly.GetType("ContextManager.Mcp.Tools.GraphSyncRunner")!
            .GetMethod("FormatDiagnostics", BindingFlags.NonPublic | BindingFlags.Static);
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
