using System.Text.Json;
using System.Text.RegularExpressions;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Tools;
using Microsoft.Build.Locator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class ProjectScanToolTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

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
        var tool = CreateTool();
        var solutionPath = FindRepoSolutionPath();
        var graphJsonPath = Path.Combine(
            Path.GetDirectoryName(solutionPath)!,
            ".context-manager",
            "graph.json");

        // Clean up any leftover file so the test verifies directory creation.
        if (File.Exists(graphJsonPath))
            File.Delete(graphJsonPath);

        var result = await tool.ProjectScanAsync(solutionPath);

        // Must match "Scan complete. N nodes, M edges." format.
        Assert.IsTrue(
            Regex.IsMatch(result, @"^Scan complete\. \d+ nodes, \d+ edges\.$"),
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
}
