using System.Text.Json;
using ContextManager.Analysis;
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
    public async Task GraphGetDependencies_KnownNode_ReturnsAggregatedContractsWithEdgeKinds()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        // Node A has out-edges to B (CALLS) and D (INJECTS), no in-edges
        var json = await tool.GraphGetDependenciesAsync("A");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        Assert.AreEqual(2, contracts!.Count);

        Assert.IsTrue(contracts.Any(c => c.Id == "B" && c.Direction == "out" && c.EdgeKinds["CALLS"] == 1),
            "B outbound with one CALLS edge must be present");
        Assert.IsTrue(contracts.Any(c => c.Id == "D" && c.Direction == "out" && c.EdgeKinds["INJECTS"] == 1),
            "D outbound with one INJECTS edge must be present");
    }

    [TestMethod]
    public async Task GraphGetDependencies_NodeWithIncomingEdge_IncludesSourceNeighborWithInDirection()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        // Node D has in-edges from A (INJECTS) and B (INJECTS), no out-edges
        var json = await tool.GraphGetDependenciesAsync("D");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        Assert.AreEqual(2, contracts!.Count);

        Assert.IsTrue(contracts.All(c => c.Direction == "in"),
            "All entries for D must have direction 'in'");
        Assert.IsTrue(contracts.All(c => c.EdgeKinds["INJECTS"] == 1),
            "All entries for D must aggregate one INJECTS edge");
        var ids = contracts.Select(c => c.Id).OrderBy(x => x).ToList();
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, ids);
    }

    [TestMethod]
    public async Task GraphGetDependencies_ContractHasCorrectKindAndEdgeKindCounts()
    {
        var tool = new GraphGetDependenciesTool(BuildSampleStore());

        var json = await tool.GraphGetDependenciesAsync("A");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);

        var dContract = contracts!.Single(c => c.Id == "D");
        Assert.AreEqual("Interface", dContract.Kind);
        Assert.AreEqual("out", dContract.Direction);
        Assert.AreEqual(1, dContract.EdgeKinds["INJECTS"]);

        var bContract = contracts!.Single(c => c.Id == "B");
        Assert.AreEqual("Class", bContract.Kind);
        Assert.AreEqual("out", bContract.Direction);
        Assert.AreEqual(1, bContract.EdgeKinds["CALLS"]);
    }

    [TestMethod]
    public async Task GraphGetDependencies_NeighborWithBothInAndOutEdges_AppearsAsOneEntryPerDirection()
    {
        // Graph: A --IMPLEMENTS--> I (out from A)
        //        B --INJECTS--> A   (in to A)
        // For node A: out-entry {I, IMPLEMENTS:1}, in-entry {B, INJECTS:1}
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var i = new GraphNode("I", "Interface");
        var b = new GraphNode("B", "Class");
        store.AddEdge(new GraphEdge(a, i, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(b, a, "INJECTS"));

        var tool = new GraphGetDependenciesTool(store);
        var json = await tool.GraphGetDependenciesAsync("A");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        Assert.AreEqual(2, contracts!.Count);

        Assert.IsTrue(contracts.Any(c => c.Id == "I" && c.Direction == "out" && c.EdgeKinds["IMPLEMENTS"] == 1),
            "I outbound via IMPLEMENTS must be present");
        Assert.IsTrue(contracts.Any(c => c.Id == "B" && c.Direction == "in" && c.EdgeKinds["INJECTS"] == 1),
            "B inbound via INJECTS must be present");
    }

    [TestMethod]
    public async Task GraphGetDependencies_TypeConsumedViaMethodCalls_SurfacesConsumersAndDropsContains()
    {
        // Static-helper coupling: consumers only CALL into Helper's method node.
        // The type query must surface them rolled up, with no CONTAINS noise.
        var store = new GraphStore();
        var helper = new GraphNode("Helper", "Class");
        var helperRun = new GraphNode("Helper.Run", "Method");
        var consumer = new GraphNode("Consumer", "Class");
        var consumerUse = new GraphNode("Consumer.Use", "Method");
        store.AddEdge(new GraphEdge(helper, helperRun, "CONTAINS"));
        store.AddEdge(new GraphEdge(consumer, consumerUse, "CONTAINS"));
        store.AddEdge(new GraphEdge(consumerUse, helperRun, "CALLS"));

        var tool = new GraphGetDependenciesTool(store);
        var json = await tool.GraphGetDependenciesAsync("Helper");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        Assert.AreEqual(1, contracts!.Count);
        Assert.AreEqual("Consumer", contracts[0].Id);
        Assert.AreEqual("in", contracts[0].Direction);
        Assert.AreEqual(1, contracts[0].EdgeKinds["CALLS"]);
        Assert.IsFalse(contracts.Any(c => c.EdgeKinds.ContainsKey("CONTAINS")),
            "CONTAINS edges must never appear in the aggregated output");
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
        // Bug 3 lock-in: the implementor Impl must now appear alongside the direct consumer X.
        CollectionAssert.AreEquivalent(new[] { "Impl", "X" }, result.AffectedIds.ToList());
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
        // Bug 3 lock-in: ConcreteClass is the start node (its outbound interface IService is
        // bridged out of the result), but X — which injects IService — surfaces transitively
        // through that bridged interface.
        CollectionAssert.AreEquivalent(new[] { "X" }, result.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceWithImplementors_IncludesImplementorsInAffectedIds()
    {
        // Bug 3: an interface's implementors must appear in its impact set.
        // Graph: ImplA --IMPLEMENTS--> IService
        //        ImplB --IMPLEMENTS--> IService
        //        X --INJECTS--> IService
        var store = new GraphStore();
        var implA = new GraphNode("ImplA", "Class");
        var implB = new GraphNode("ImplB", "Class");
        var x = new GraphNode("X", "Class");
        var iface = new GraphNode("IService", "Interface");
        store.AddEdge(new GraphEdge(implA, iface, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(implB, iface, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(x, iface, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IService");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "ImplA", "ImplB", "X" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_FoundationalInterfaceNoDirectConsumers_ReturnsImplementorsAndTransitiveConsumers()
    {
        // Bug 4: IEntity has no direct REFERENCES/INJECTS consumers, but its implementors
        // (and their downstream consumers) must surface. The result must be non-empty.
        // Graph: User  --IMPLEMENTS--> IEntity
        //        Order --IMPLEMENTS--> IEntity
        //        Service --INJECTS--> User   (transitive consumer of an implementor)
        var store = new GraphStore();
        var user = new GraphNode("User", "Class");
        var order = new GraphNode("Order", "Class");
        var service = new GraphNode("Service", "Class");
        var entity = new GraphNode("IEntity", "Interface");
        store.AddEdge(new GraphEdge(user, entity, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(order, entity, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(service, user, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IEntity");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.AffectedIds.Count > 0, "Foundational interface must not return empty.");
        CollectionAssert.AreEquivalent(
            new[] { "User", "Order", "Service" }, result.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceReachedTransitively_PullsInItsImplementors()
    {
        // Implementors must be reached even when the interface is hit mid-BFS, not as start node.
        // Graph: Impl --IMPLEMENTS--> ITransitive
        //        ITransitive --INJECTS--> IStart   (so ITransitive is reached backward from IStart)
        var store = new GraphStore();
        var impl = new GraphNode("Impl", "Class");
        var iTransitive = new GraphNode("ITransitive", "Interface");
        var iStart = new GraphNode("IStart", "Interface");
        store.AddEdge(new GraphEdge(impl, iTransitive, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(iTransitive, iStart, "INJECTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IStart");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        // ITransitive is a backward consumer; once dequeued, its implementor Impl must appear.
        CollectionAssert.AreEquivalent(
            new[] { "ITransitive", "Impl" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_DiamondImplementsGraph_TerminatesWithNoDuplicates()
    {
        // Diamond/cyclic IMPLEMENTS reach must terminate with no duplicate entries.
        // Graph: IDerived --IMPLEMENTS--> IBase   (interface-to-interface)
        //        Impl --IMPLEMENTS--> IBase
        //        Impl --IMPLEMENTS--> IDerived
        // Querying IBase reaches IDerived (implementor) and Impl from two paths.
        var store = new GraphStore();
        var iBase = new GraphNode("IBase", "Interface");
        var iDerived = new GraphNode("IDerived", "Interface");
        var impl = new GraphNode("Impl", "Class");
        store.AddEdge(new GraphEdge(iDerived, iBase, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(impl, iBase, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(impl, iDerived, "IMPLEMENTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("IBase");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        var ids = result!.AffectedIds.ToList();
        Assert.AreEqual(ids.Distinct().Count(), ids.Count, "No duplicate entries allowed.");
        CollectionAssert.AreEquivalent(new[] { "IDerived", "Impl" }, ids);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_OutboundBridgedInterfaceStaysOutOfResult_ImplementorsStayIn()
    {
        // Result-vs-routing invariant: an interface bridged OUTBOUND (the start class implements it)
        // is routing-only and must stay OUT of the result; a class implementing that interface
        // (inbound) must be IN the result.
        // Graph: Start --IMPLEMENTS--> IShared   (outbound bridge from Start → IShared excluded)
        //        Other --IMPLEMENTS--> IShared   (inbound implementor → included)
        var store = new GraphStore();
        var start = new GraphNode("Start", "Class");
        var other = new GraphNode("Other", "Class");
        var iShared = new GraphNode("IShared", "Interface");
        store.AddEdge(new GraphEdge(start, iShared, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(other, iShared, "IMPLEMENTS"));

        var tool = new GraphImpactAnalysisTool(store);

        var json = await tool.GraphImpactAnalysisAsync("Start");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        // IShared is bridged outbound (seed) → excluded; Other is an implementor → included.
        CollectionAssert.AreEquivalent(new[] { "Other" }, result!.AffectedIds.ToList());
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
