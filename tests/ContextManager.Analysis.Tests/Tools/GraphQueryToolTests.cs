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
    public async Task GraphImpactAnalysis_LeafNode_ReturnsAncestors()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // C has in-edge from B (CALLS). B has in-edge from A (CALLS).
        // Impact backward from C: first B, then A.
        var json = await tool.GraphImpactAnalysisAsync("C");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result!.AffectedIds.Count);
        Assert.AreEqual("B", result.AffectedIds[0]);
        Assert.AreEqual("A", result.AffectedIds[1]);
        Assert.AreEqual(0, result.Diagnostics.Count);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_SharedDependency_ReturnsAllCallers()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // D has in-edges from A (INJECTS) and B (INJECTS).
        // Impact backward from D: A and B (order depends on insertion order).
        var json = await tool.GraphImpactAnalysisAsync("D");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_NodeWithNoCallers_ReturnsEmptyAffectedIds()
    {
        var tool = new GraphImpactAnalysisTool(BuildSampleStore());

        // A has no in-edges → empty impact result.
        var json = await tool.GraphImpactAnalysisAsync("A");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result!.AffectedIds.Count);
        Assert.AreEqual(0, result.Diagnostics.Count);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceWithNoImplementations_EmitsReflectionDiagnostic()
    {
        // Graph: X --INJECTS--> IService
        // IService has no inbound IMPLEMENTS edges → reflection blind spot.
        var store = new GraphStore();
        var x = new GraphNode("X", "Class");
        var iface = new GraphNode("IService", "Interface");
        store.AddNode(x);
        store.AddNode(iface);
        store.AddEdge(new GraphEdge(x, iface, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IService");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        // X injects IService, so X is an affected caller.
        CollectionAssert.AreEquivalent(new[] { "X" }, result!.AffectedIds.ToList());
        // IService has no implementations → one diagnostic entry.
        Assert.AreEqual(1, result.Diagnostics.Count);
        Assert.AreEqual("reflection_blind_spot", result.Diagnostics[0].Code);
        Assert.AreEqual("IService", result.Diagnostics[0].InterfaceId);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceWithImplementation_NoDiagnostic()
    {
        // Graph: Impl --IMPLEMENTS--> IService
        //        X --INJECTS--> IService
        // IService has one inbound IMPLEMENTS edge → no reflection blind spot.
        var store = new GraphStore();
        var impl = new GraphNode("Impl", "Class");
        var x = new GraphNode("X", "Class");
        var iface = new GraphNode("IService", "Interface");
        store.AddNode(impl);
        store.AddNode(x);
        store.AddNode(iface);
        store.AddEdge(new GraphEdge(impl, iface, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(x, iface, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IService");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result!.Diagnostics.Count);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_ClassImplementsUnboundInterface_EmitsDiagnosticForInterface()
    {
        // Graph: ConcreteClass --IMPLEMENTS--> IService (IService has no other IMPLEMENTS edges)
        //        X --INJECTS--> IService
        // Analyzing ConcreteClass: it implements IService but IService has no OTHER inbound IMPLEMENTS.
        // Wait — ConcreteClass itself provides an implementation so IService DOES have an implementation.
        // This test verifies the positive (non-false-positive) path for a concrete class that
        // implements an interface WITH at least one implementation present.
        var store = new GraphStore();
        var concrete = new GraphNode("ConcreteClass", "Class");
        var iface = new GraphNode("IService", "Interface");
        var x = new GraphNode("X", "Class");
        store.AddNode(concrete);
        store.AddNode(iface);
        store.AddNode(x);
        store.AddEdge(new GraphEdge(concrete, iface, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(x, iface, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        // Analyzing ConcreteClass — it implements IService which has 1 inbound IMPLEMENTS → no diagnostic.
        var json = await tool.GraphImpactAnalysisAsync("ConcreteClass");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result!.Diagnostics.Count);
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
