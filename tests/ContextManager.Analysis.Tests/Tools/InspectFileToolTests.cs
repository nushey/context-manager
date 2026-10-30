using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class InspectFileToolTests : FixtureTest
{
    private static string FixturePath(string name)
        => Path.Combine(
            Path.GetDirectoryName(typeof(InspectFileToolTests).Assembly.Location)!,
            "Fixtures",
            name);

    private static readonly InspectFileTool _tool = new(new FileAnalyzer());

    [TestMethod]
    public async Task AnalyzeAsync_Compact_OmitsVerboseFieldsAndKeepsMethodOneLiners()
    {
        var json = await _tool.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), compact: true, CancellationToken.None);

        // Methods survive as one-liner signatures (method name is present).
        Assert.IsTrue(json.Contains("ProcessOrder"), $"Compact output should carry the method one-liner. Got: {json}");
        // Verbose per-method detail is dropped.
        Assert.IsFalse(json.Contains("startLine"), $"Compact output must omit startLine. Got: {json}");
        // Properties are dropped in compact mode.
        Assert.IsFalse(json.Contains("OrderCount"), $"Compact output must omit properties. Got: {json}");
    }

    [TestMethod]
    public async Task AnalyzeAsync_Full_KeepsVerboseFields()
    {
        var json = await _tool.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), compact: false, CancellationToken.None);

        Assert.IsTrue(json.Contains("startLine"), $"Full output should contain startLine. Got: {json}");
        Assert.IsTrue(json.Contains("OrderCount"), $"Full output should contain properties. Got: {json}");
    }

    private static string WriteSource(string directory)
    {
        var path = Path.Combine(directory, "Service.cs");
        File.WriteAllText(path, "namespace App;\n\npublic class Service { }\n\npublic class Unscanned { }\n");
        return path;
    }

    [TestMethod]
    public async Task AnalyzeAsync_GraphPersisted_AttachesTypeGraphContextInBothModes()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "App.sln"), string.Empty);
        var source = WriteSource(directory);
        var store = new GraphStore();
        var service = new GraphNode("App.Service", "Class", source);
        for (var i = 0; i < 7; i++)
            store.AddEdge(new GraphEdge(new GraphNode($"App.Caller{i}", "Class", $"/src/Caller{i}.cs"), service, "INJECTS"));
        store.AddEdge(new GraphEdge(service, new GraphNode("App.Repository", "Interface", "/src/Repository.cs"), "CALLS"));
        var graphPath = GraphRegistry.GetGraphPath(Path.Combine(directory, "App.sln"));
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, store.Serialize());
        var tool = new InspectFileTool(new FileAnalyzer(), new GraphEnricher(new GraphRegistry()));

        var full = JsonSerializer.Deserialize<FileAnalysis>(await tool.AnalyzeAsync(source), AnalysisJson.Options)!;
        var compact = JsonSerializer.Deserialize<CompactFileAnalysis>(await tool.AnalyzeAsync(source, compact: true), AnalysisJson.Options)!;

        var graph = full.Types.Single(t => t.Name == "Service").Graph!;
        Assert.AreEqual("App.Service", graph.NodeId);
        Assert.AreEqual(7, graph.InboundCount);
        Assert.AreEqual(1, graph.OutboundCount);
        CollectionAssert.AreEqual(Enumerable.Range(0, 5).Select(i => $"App.Caller{i}").ToList(), graph.DirectCallers!.Select(e => e.Id).ToList());
        Assert.AreEqual("/src/Caller0.cs", graph.DirectCallers![0].File);
        Assert.AreEqual("/src/Repository.cs", graph.DirectDependencies!.Single().File);
        Assert.IsNull(full.Types.Single(t => t.Name == "Unscanned").Graph);
        Assert.IsNull(full.GraphStatus);
        Assert.AreEqual("App.Service", compact.Types.Single(t => t.Name == "Service").Graph?.NodeId);
    }

    [TestMethod]
    public async Task AnalyzeAsync_GraphPersisted_DirectCallersExcludeImplementors()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "App.sln"), string.Empty);
        var source = WriteSource(directory);
        var store = new GraphStore();
        var service = new GraphNode("App.Service", "Class", source);
        store.AddEdge(new GraphEdge(new GraphNode("App.Implementation", "Class", "/src/Implementation.cs"), service, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(new GraphNode("App.Consumer", "Class", "/src/Consumer.cs"), service, "INJECTS"));
        var graphPath = GraphRegistry.GetGraphPath(Path.Combine(directory, "App.sln"));
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, store.Serialize());
        var tool = new InspectFileTool(new FileAnalyzer(), new GraphEnricher(new GraphRegistry()));

        var full = JsonSerializer.Deserialize<FileAnalysis>(await tool.AnalyzeAsync(source), AnalysisJson.Options)!;

        var graph = full.Types.Single(t => t.Name == "Service").Graph!;
        Assert.AreEqual(2, graph.InboundCount);
        CollectionAssert.AreEqual(new[] { "App.Consumer" }, graph.DirectCallers!.Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public async Task AnalyzeAsync_SolutionWithoutGraph_ReportsNotInitializedWithSolutionPath()
    {
        var directory = NewTempDirectory();
        var solution = Path.Combine(directory, "App.sln");
        File.WriteAllText(solution, string.Empty);
        var nested = Directory.CreateDirectory(Path.Combine(directory, "src")).FullName;
        var source = WriteSource(nested);
        var tool = new InspectFileTool(new FileAnalyzer(), new GraphEnricher(new GraphRegistry()));

        var json = await tool.AnalyzeAsync(source, compact: true);

        Assert.AreEqual($"not_initialized (run project_scan on {solution})",
            JsonSerializer.Deserialize<CompactFileAnalysis>(json, AnalysisJson.Options)!.GraphStatus);
    }

    [TestMethod]
    public async Task AnalyzeAsync_TwoSolutionsWithoutGraph_AttachesNothing()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "A.sln"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "B.sln"), string.Empty);
        var source = WriteSource(directory);
        var tool = new InspectFileTool(new FileAnalyzer(), new GraphEnricher(new GraphRegistry()));

        var analysis = JsonSerializer.Deserialize<FileAnalysis>(await tool.AnalyzeAsync(source), AnalysisJson.Options)!;

        Assert.IsNull(analysis.GraphStatus);
        Assert.IsTrue(analysis.Types.All(t => t.Graph is null));
    }

    [TestMethod]
    public async Task AnalyzeAsync_UnreadableGraph_KeepsAnalysisWithoutGraphData()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "App.sln"), string.Empty);
        var source = WriteSource(directory);
        var graphPath = GraphRegistry.GetGraphPath(Path.Combine(directory, "App.sln"));
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, "{ not json");
        var tool = new InspectFileTool(new FileAnalyzer(), new GraphEnricher(new GraphRegistry()));

        var analysis = JsonSerializer.Deserialize<FileAnalysis>(await tool.AnalyzeAsync(source), AnalysisJson.Options)!;

        Assert.IsNull(analysis.GraphStatus);
        Assert.AreEqual(2, analysis.Types.Count);
        Assert.IsTrue(analysis.Types.All(t => t.Graph is null));
    }
}
