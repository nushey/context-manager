using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Analysis.Tests.Graph;
using ContextManager.Mcp;
using ContextManager.Mcp.Serialization;
using ContextManager.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class GraphSearchToolTests : FixtureTest
{
    private sealed class StubSynchronizer(GraphStore? graph = null, Exception? error = null) : IGraphSynchronizer
    {
        public int Calls { get; private set; }

        public Task<GraphSyncResult?> SynchronizeAsync(string solutionPath, bool reevaluate, CancellationToken ct = default)
        {
            Calls++;
            if (error is not null)
                throw error;
            return Task.FromResult(graph is null ? null : new GraphSyncResult(graph,
                new GraphBuildResult("complete", 1, 1, 0, 0, 1, 1, 0, graph.NodeCount, graph.EdgeCount)));
        }
    }

    [TestMethod]
    [DataRow(new[] { "Login", "ab" })]
    [DataRow(new string[0])]
    [DataRow(new[] { "Login", "     " })]
    [DataRow(new[] { " ab " })]
    public async Task InvalidTerms_RejectEntireQueryBeforeSynchronizing(string[] queries)
    {
        var sync = new StubSynchronizer();
        var json = await new GraphSearchTool(sync).GraphSearchAsync("/test.sln", queries);
        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options)!;
        Assert.AreEqual("invalid_query", error.Code);
        Assert.AreEqual("Provide at least one query term; each term must be at least 3 characters long after trimming whitespace.", error.Message);
        Assert.AreEqual(0, sync.Calls);
    }

    [TestMethod]
    public async Task Terms_AreTrimmedBeforeMatching()
    {
        var tool = new GraphSearchTool(new StubSynchronizer(await GraphSearchTests.FixtureGraphAsync()));
        Assert.AreEqual(await tool.GraphSearchAsync("/test.sln", ["Login"]), await tool.GraphSearchAsync("/test.sln", ["  Login "]));
    }

    [TestMethod]
    public async Task Contract_IsCompactWithRequiredArraysAndTruncation()
    {
        var graph = await GraphSearchTests.FixtureGraphAsync();
        var tool = new GraphSearchTool(new StubSynchronizer(graph));
        var json = await tool.GraphSearchAsync("/test.sln", ["SearchGlobal"]);
        Assert.IsFalse(json.Contains('\n'));
        using var parsed = JsonDocument.Parse(json);
        var file = parsed.RootElement.GetProperty("results")[0];
        CollectionAssert.AreEqual(new[] { "file", "namespaces", "fileMatched", "methods" }, file.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(0, file.GetProperty("namespaces").GetArrayLength());
        Assert.AreEqual(0, file.GetProperty("methods").GetArrayLength());
        Assert.IsTrue(file.GetProperty("fileMatched").GetBoolean());
        Assert.AreEqual("{\"results\":[],\"truncated\":false}", await tool.GraphSearchAsync("/test.sln", ["Missing"]));
        var methodJson = await tool.GraphSearchAsync("/test.sln", ["Login"]);
        using var methods = JsonDocument.Parse(methodJson);
        var methodFile = methods.RootElement.GetProperty("results")[0];
        Assert.IsFalse(methodFile.GetProperty("fileMatched").GetBoolean());
        CollectionAssert.AreEqual(new[] { "name", "nodeId" }, methodFile.GetProperty("methods")[0].EnumerateObject().Select(p => p.Name).ToArray());
        var truncated = await new GraphSearchTool(new StubSynchronizer(GraphSearchTests.Methods(51)))
            .GraphSearchAsync("/test.sln", ["Login"]);
        var contract = JsonSerializer.Deserialize<GraphSearchResultContract>(truncated, AnalysisJson.Options)!;
        Assert.IsTrue(contract.Truncated);
        Assert.AreEqual(50, contract.Results.Single().Methods.Count);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task RegisteredTool_BuildsAndSynchronizesRenamedMethodsAndNamespaces()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solution = Path.Combine(directory, "Incremental.sln");
        var services = Own(new ServiceCollection().AddContextManagerServices().BuildServiceProvider());
        var tool = ActivatorUtilities.CreateInstance<GraphSearchTool>(services);
        var initial = await tool.GraphSearchAsync(solution, ["Sender", "Record"]);
        Assert.IsTrue(File.Exists(GraphRegistry.GetGraphPath(solution)));
        Assert.IsTrue(JsonSerializer.Deserialize<GraphSearchResultContract>(initial, AnalysisJson.Options)!.Results.Count > 0, initial);
        var audit = Path.Combine(directory, "Core", "Audit.cs");
        FixtureCopy.Edit(audit, "Record", "RecordRenamed");
        FixtureCopy.Edit(audit, "IncrementalFixtures.Core", "IncrementalFixtures.Updated");
        var updated = await tool.GraphSearchAsync(solution, ["RecordRenamed"]);
        var file = JsonSerializer.Deserialize<GraphSearchResultContract>(updated, AnalysisJson.Options)!.Results.Single();
        CollectionAssert.Contains(file.Namespaces.ToArray(), "IncrementalFixtures.Updated");
        Assert.IsTrue(file.Methods.Single().NodeId.Contains("Updated.Audit.RecordRenamed"), updated);
        var old = await tool.GraphSearchAsync(solution, ["Record"]);
        Assert.IsFalse(old.Contains("Audit.Record()", StringComparison.Ordinal), old);
    }

    [TestMethod]
    public async Task SharedSynchronizationErrors_AreReturnedUnchanged()
    {
        var missing = await new GraphSearchTool(CreateSynchronizer(new GraphRegistry()))
            .GraphSearchAsync(Path.Combine(NewTempDirectory(), "Missing.sln"), ["Login"]);
        Assert.AreEqual("graph_not_found", JsonSerializer.Deserialize<AnalysisError>(missing, AnalysisJson.Options)!.Code);
        foreach (var phase in new[] { "msbuild_registration", "solution_evaluation_and_extraction", "graph_load" })
        {
            var json = await new GraphSearchTool(new StubSynchronizer(error: new GraphSyncException(phase, new InvalidOperationException("Failure"))))
                .GraphSearchAsync("/test.sln", ["Login"]);
            Assert.AreEqual(phase == "msbuild_registration" ? "msbuild_not_found" : "scan_failed",
                JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options)!.Code);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await new GraphSearchTool(new StubSynchronizer(error: new OperationCanceledException(cancelled.Token)))
            .GraphSearchAsync("/test.sln", ["Login"], cancelled.Token);
        Assert.AreEqual("scan_cancelled", JsonSerializer.Deserialize<AnalysisError>(cancellation, AnalysisJson.Options)!.Code);
    }
}
