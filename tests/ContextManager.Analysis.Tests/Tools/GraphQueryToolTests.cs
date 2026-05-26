using System.Text.Json;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

/// <summary>
/// Tests for graph query tools: GraphGetDependenciesTool, GraphImpactAnalysisTool, GraphPathFindTool.
/// All graphs are built by hand — no Roslyn or MSBuild involved.
/// </summary>
[TestClass]
public class GraphQueryToolTests
{
    // Hand-built graph:
    //
    //   A --CALLS--> B --CALLS--> C
    //   A --INJECTS--> D
    //   B --INJECTS--> D
    //
    private static GraphStore BuildSampleStore()
    {
        var store = new GraphStore();

        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        var c = new GraphNode("C", "Class");
        var d = new GraphNode("D", "Interface");

        store.AddNode(a);
        store.AddNode(b);
        store.AddNode(c);
        store.AddNode(d);

        store.AddEdge(new GraphEdge(a, b, "CALLS"));
        store.AddEdge(new GraphEdge(b, c, "CALLS"));
        store.AddEdge(new GraphEdge(a, d, "INJECTS"));
        store.AddEdge(new GraphEdge(b, d, "INJECTS"));

        return store;
    }

    // ── GraphGetDependenciesTool ─────────────────────────────────────────────

    [TestMethod]
    public async Task GraphGetDependencies_UnknownNode_ReturnsNodeNotFoundError()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        var json = await tool.GraphGetDependenciesAsync("UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.AreEqual("UNKNOWN", error.FilePath);
    }

    [TestMethod]
    public async Task GraphGetDependencies_KnownNode_ReturnsNeighborContracts()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        // Node A has out-edges to B and D, no in-edges → neighbors: B, D
        var json = await tool.GraphGetDependenciesAsync("A");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        var ids = contracts!.Select(c => c.Id).OrderBy(x => x).ToList();
        CollectionAssert.AreEquivalent(new[] { "B", "D" }, ids);
    }

    [TestMethod]
    public async Task GraphGetDependencies_NodeWithIncomingEdge_IncludesSourceNeighbor()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        // Node D has in-edges from A and B, no out-edges → neighbors: A, B
        var json = await tool.GraphGetDependenciesAsync("D");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        var ids = contracts!.Select(c => c.Id).OrderBy(x => x).ToList();
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, ids);
    }

    [TestMethod]
    public async Task GraphGetDependencies_ContractHasCorrectKind()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        var json = await tool.GraphGetDependenciesAsync("A");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);

        var dContract = contracts!.Single(c => c.Id == "D");
        Assert.AreEqual("Interface", dContract.Kind);

        var bContract = contracts!.Single(c => c.Id == "B");
        Assert.AreEqual("Class", bContract.Kind);
    }

    // ── GraphImpactAnalysisTool ──────────────────────────────────────────────

    [TestMethod]
    public async Task GraphImpactAnalysis_UnknownNode_ReturnsNodeNotFoundError()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        var json = await tool.GraphImpactAnalysisAsync("UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.AreEqual("UNKNOWN", error.FilePath);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_LeafNode_ReturnsBfsAncestors()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // C has in-edge from B (CALLS). B has in-edge from A (CALLS).
        // BFS backward from C: first B, then A.
        var json = await tool.GraphImpactAnalysisAsync("C");

        var ids = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(ids);
        Assert.AreEqual(2, ids!.Count);
        Assert.AreEqual("B", ids[0]);
        Assert.AreEqual("A", ids[1]);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_SharedDependency_ReturnsAllCallers()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // D has in-edges from A (INJECTS) and B (INJECTS).
        // BFS backward from D: A and B (order depends on insertion order).
        var json = await tool.GraphImpactAnalysisAsync("D");

        var ids = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(ids);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, ids!);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_NodeWithNoCallers_ReturnsEmptyList()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // A has no in-edges → empty BFS result.
        var json = await tool.GraphImpactAnalysisAsync("A");

        var ids = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(ids);
        Assert.AreEqual(0, ids!.Count);
    }

    // ── GraphPathFindTool ────────────────────────────────────────────────────

    [TestMethod]
    public async Task GraphPathFind_UnknownSource_ReturnsNodeNotFoundError()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        var json = await tool.GraphPathFindAsync("UNKNOWN", "B");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.AreEqual("UNKNOWN", error.FilePath);
    }

    [TestMethod]
    public async Task GraphPathFind_UnknownTarget_ReturnsNodeNotFoundError()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        var json = await tool.GraphPathFindAsync("A", "UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.AreEqual("UNKNOWN", error.FilePath);
    }

    [TestMethod]
    public async Task GraphPathFind_NoDirectedPath_ReturnsNoPathError()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        // C → A: no directed path exists (edges go A→B→C, not backward).
        var json = await tool.GraphPathFindAsync("C", "A");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("no_path", error!.Code);
    }

    [TestMethod]
    public async Task GraphPathFind_DirectPath_ReturnsCorrectSequence()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        // A → C: A --CALLS--> B --CALLS--> C
        var json = await tool.GraphPathFindAsync("A", "C");

        var path = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(path);
        Assert.AreEqual(3, path!.Count);
        Assert.AreEqual("A", path[0]);
        Assert.AreEqual("B", path[1]);
        Assert.AreEqual("C", path[2]);
    }

    [TestMethod]
    public async Task GraphPathFind_SameSourceAndTarget_ReturnsSingleElement()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        var json = await tool.GraphPathFindAsync("A", "A");

        var path = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(path);
        Assert.AreEqual(1, path!.Count);
        Assert.AreEqual("A", path[0]);
    }

    [TestMethod]
    public async Task GraphPathFind_AdjacentNodes_ReturnsTwoElementPath()
    {
        var tool = new GraphPathFindTool(BuildSampleStore());

        var json = await tool.GraphPathFindAsync("A", "B");

        var path = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(path);
        Assert.AreEqual(2, path!.Count);
        Assert.AreEqual("A", path[0]);
        Assert.AreEqual("B", path[1]);
    }
}
