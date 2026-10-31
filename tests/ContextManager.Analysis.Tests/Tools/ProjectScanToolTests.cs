using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Tests.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class ProjectScanToolTests : FixtureTest
{
    private ProjectScanTool CreateTool(GraphRegistry? registry = null) =>
        new(CreateSynchronizer(registry ?? new GraphRegistry()));

    private static void PersistPreviousGraph(string solutionPath)
    {
        var graphPath = GraphRegistry.GetGraphPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, new GraphStore().WithNode(new GraphNode("Previous", "Class")).Serialize());
    }

    private static bool HasPreviousGraph(string solutionPath) =>
        new GraphRegistry().TryLoad(solutionPath)!.TryGetNode("Previous", out _);

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

        StringAssert.StartsWith(result, "Scan partial. 16 nodes, 19 edges. Coverage:");
        var persisted = new GraphStore();
        persisted.Deserialize(await File.ReadAllTextAsync(GraphRegistry.GetGraphPath(solutionPath)));
        Assert.AreEqual(16, persisted.NodeCount);
        Assert.AreEqual(19, persisted.EdgeCount);
        Assert.IsTrue(persisted.TryGetNode("IncrementalFixtures.Consumer.Client", out _));
    }

    [TestMethod]
    public async Task ProjectScanAsync_PreCancelled_PreservesPublishedGraph()
    {
        var solutionPath = CopyIncrementalSolution();
        PersistPreviousGraph(solutionPath);
        var tool = CreateTool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var json = await tool.ProjectScanAsync(solutionPath, cts.Token);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_cancelled", error?.Code);
        Assert.IsTrue(HasPreviousGraph(solutionPath));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_MixedLegacyAndMissingTargets_ReportsPartialCoverage()
    {
        var tool = CreateTool();
        var solutionPath = CopyCoverageSolution();

        var result = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(result, "Scan partial.");
        StringAssert.Contains(result, "unsupported projects");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ProjectScanAsync_EmptyScope_ReturnsDedicatedErrorAndPreservesSnapshot()
    {
        var directory = CopyFixture("ScanFixtures");
        var solutionPath = Path.Combine(directory, "Empty.sln");
        PersistPreviousGraph(solutionPath);
        var tool = CreateTool();

        var json = await tool.ProjectScanAsync(solutionPath);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.AreEqual("scan_empty", error?.Code);
        Assert.IsTrue(HasPreviousGraph(solutionPath));
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ProjectScanAsync_SecondScanAfterEdit_UpdatesGraphIncrementally()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var registry = new GraphRegistry();
        var tool = CreateTool(registry);

        StringAssert.StartsWith(await tool.ProjectScanAsync(solutionPath), "Scan partial. 16 nodes, 19 edges.");
        var firstRevision = registry.TryLoad(solutionPath)!.Revision;
        var sender = Path.Combine(directory, "Core", "Sender.cs");
        FixtureCopy.Edit(sender, "        Audit.Record();\n", "");

        var second = await tool.ProjectScanAsync(solutionPath);

        StringAssert.StartsWith(second, "Scan partial. 16 nodes, 18 edges.");
        var graph = registry.TryLoad(solutionPath)!;
        Assert.AreNotEqual(firstRevision, graph.Revision);
        Assert.IsFalse(graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Sender").Any(n => n.Id == "IncrementalFixtures.Core.Audit"));
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
}
