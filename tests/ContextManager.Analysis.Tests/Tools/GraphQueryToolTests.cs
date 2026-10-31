using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Tests.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp;
using ContextManager.Mcp.Serialization;
using ContextManager.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class GraphQueryToolTests : FixtureTest
{
    private string PersistGraph(GraphStore store)
    {
        var solutionPath = Path.Combine(NewTempDirectory(), "Sample.sln");
        WriteGraph(solutionPath, store);
        return solutionPath;
    }

    private static void WriteGraph(string solutionPath, GraphStore store)
    {
        var graphPath = GraphRegistry.GetGraphPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, store.Serialize());
    }

    [TestMethod]
    public async Task GraphQueryTools_NoGraphJson_ReturnGraphNotFound()
    {
        var registry = new GraphRegistry();
        var solutionPath = Path.Combine(Path.GetTempPath(), $"context-manager-missing-{Guid.NewGuid():N}", "Missing.sln");

        var results = new[]
        {
            await new GraphGetDependenciesTool(new PersistedGraphSynchronizer(registry)).GraphGetDependenciesAsync(solutionPath, "A"),
            await new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(registry)).GraphImpactAnalysisAsync(solutionPath, "A"),
            await new GraphPathFindTool(new PersistedGraphSynchronizer(registry)).GraphPathFindAsync(solutionPath, "A", "B"),
        };

        foreach (var json in results)
        {
            var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
            Assert.AreEqual("graph_not_found", error?.Code);
            Assert.AreEqual(solutionPath, error?.FilePath);
        }
    }

    [TestMethod]
    public async Task GraphGetDependencies_GraphJsonRewrittenByAnotherSession_ReloadsOnNextQuery()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));
        var before = await tool.GraphGetDependenciesAsync(solutionPath, "Fresh");
        Assert.AreEqual("node_not_found", JsonSerializer.Deserialize<AnalysisError>(before, AnalysisJson.Options)?.Code);

        var rescanned = BuildSampleStore();
        rescanned = rescanned.WithEdge(new GraphEdge(new GraphNode("Fresh", "Class"), new GraphNode("A", "Class"), "CALLS"));
        WriteGraph(solutionPath, rescanned);
        File.SetLastWriteTimeUtc(GraphRegistry.GetGraphPath(solutionPath), DateTime.UtcNow.AddMinutes(1));

        var after = await tool.GraphGetDependenciesAsync(solutionPath, "Fresh");

        var neighbors = JsonSerializer.Deserialize<List<GraphNodeContract>>(after, AnalysisJson.Options);
        Assert.AreEqual("A", neighbors?.Single().Id);
    }

    [TestMethod]
    public async Task GraphGetDependencies_TwoSolutions_QueriesEachOwnGraph()
    {
        var other = new GraphStore();
        other = other.WithEdge(new GraphEdge(new GraphNode("A", "Class"), new GraphNode("Z", "Class"), "CALLS"));
        var sampleSolution = PersistGraph(BuildSampleStore());
        var otherSolution = PersistGraph(other);
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var sample = JsonSerializer.Deserialize<List<GraphNodeContract>>(
            await tool.GraphGetDependenciesAsync(sampleSolution, "A"), AnalysisJson.Options);
        var otherResult = JsonSerializer.Deserialize<List<GraphNodeContract>>(
            await tool.GraphGetDependenciesAsync(otherSolution, "A"), AnalysisJson.Options);

        CollectionAssert.AreEqual(new[] { "B", "D" }, sample!.Select(n => n.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Z" }, otherResult!.Select(n => n.Id).ToArray());
    }

    private static GraphStore BuildSampleStore()
    {
        var store = new GraphStore();

        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        var c = new GraphNode("C", "Class");
        var d = new GraphNode("D", "Interface");

        store = store.WithNode(a);
        store = store.WithNode(b);
        store = store.WithNode(c);
        store = store.WithNode(d);

        store = store.WithEdge(new GraphEdge(a, b, "CALLS"));
        store = store.WithEdge(new GraphEdge(b, c, "CALLS"));
        store = store.WithEdge(new GraphEdge(a, d, "INJECTS"));
        store = store.WithEdge(new GraphEdge(b, d, "INJECTS"));

        return store;
    }

    [TestMethod]
    public async Task GraphGetDependencies_UnknownNode_ReturnsNodeNotFoundError()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphGetDependenciesAsync(solutionPath, "UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.IsNull(error.FilePath);
    }

    [TestMethod]
    public async Task GraphGetDependencies_KnownNode_ReturnsAggregatedContractsWithEdgeKinds()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphGetDependenciesAsync(solutionPath, "A");

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
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphGetDependenciesAsync(solutionPath, "D");

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
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphGetDependenciesAsync(solutionPath, "A");

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
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var i = new GraphNode("I", "Interface");
        var b = new GraphNode("B", "Class");
        store = store.WithEdge(new GraphEdge(a, i, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(b, a, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));
        var json = await tool.GraphGetDependenciesAsync(solutionPath, "A");

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
        var store = new GraphStore();
        var helper = new GraphNode("Helper", "Class");
        var helperRun = new GraphNode("Helper.Run", "Method");
        var consumer = new GraphNode("Consumer", "Class");
        var consumerUse = new GraphNode("Consumer.Use", "Method");
        store = store.WithEdge(new GraphEdge(helper, helperRun, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(consumer, consumerUse, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(consumerUse, helperRun, "CALLS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry()));
        var json = await tool.GraphGetDependenciesAsync(solutionPath, "Helper");

        var contracts = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options);
        Assert.IsNotNull(contracts);
        Assert.AreEqual(1, contracts!.Count);
        Assert.AreEqual("Consumer", contracts[0].Id);
        Assert.AreEqual("in", contracts[0].Direction);
        Assert.AreEqual(1, contracts[0].EdgeKinds["CALLS"]);
        Assert.IsFalse(contracts.Any(c => c.EdgeKinds.ContainsKey("CONTAINS")),
            "CONTAINS edges must never appear in the aggregated output");
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_UnknownNode_ReturnsNodeNotFoundError()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.IsNull(error.FilePath);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_LeafNode_ReturnsAncestors()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "C");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result!.AffectedIds.Count);
        Assert.AreEqual("B", result.AffectedIds[0]);
        Assert.AreEqual("A", result.AffectedIds[1]);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_SharedDependency_ReturnsAllCallers()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "D");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_NodeWithNoCallers_ReturnsEmptyAffectedIds()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "A");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result!.AffectedIds.Count);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceWithImplementation_NoDiagnostic()
    {
        var store = new GraphStore();
        var impl = new GraphNode("Impl", "Class");
        var x = new GraphNode("X", "Class");
        var iface = new GraphNode("IService", "Interface");
        store = store.WithNode(impl);
        store = store.WithNode(x);
        store = store.WithNode(iface);
        store = store.WithEdge(new GraphEdge(impl, iface, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(x, iface, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "IService");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "Impl", "X" }, result.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_ClassImplementsUnboundInterface_EmitsDiagnosticForInterface()
    {
        var store = new GraphStore();
        var concrete = new GraphNode("ConcreteClass", "Class");
        var iface = new GraphNode("IService", "Interface");
        var x = new GraphNode("X", "Class");
        store = store.WithNode(concrete);
        store = store.WithNode(iface);
        store = store.WithNode(x);
        store = store.WithEdge(new GraphEdge(concrete, iface, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(x, iface, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "ConcreteClass");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "X" }, result.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceWithImplementors_IncludesImplementorsInAffectedIds()
    {
        var store = new GraphStore();
        var implA = new GraphNode("ImplA", "Class");
        var implB = new GraphNode("ImplB", "Class");
        var x = new GraphNode("X", "Class");
        var iface = new GraphNode("IService", "Interface");
        store = store.WithEdge(new GraphEdge(implA, iface, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(implB, iface, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(x, iface, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "IService");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "ImplA", "ImplB", "X" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_FoundationalInterfaceNoDirectConsumers_ReturnsImplementorsAndTransitiveConsumers()
    {
        var store = new GraphStore();
        var user = new GraphNode("User", "Class");
        var order = new GraphNode("Order", "Class");
        var service = new GraphNode("Service", "Class");
        var entity = new GraphNode("IEntity", "Interface");
        store = store.WithEdge(new GraphEdge(user, entity, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(order, entity, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(service, user, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "IEntity");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.AffectedIds.Count > 0, "Foundational interface must not return empty.");
        CollectionAssert.AreEquivalent(
            new[] { "User", "Order", "Service" }, result.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_InterfaceReachedTransitively_PullsInItsImplementors()
    {
        var store = new GraphStore();
        var impl = new GraphNode("Impl", "Class");
        var iTransitive = new GraphNode("ITransitive", "Interface");
        var iStart = new GraphNode("IStart", "Interface");
        store = store.WithEdge(new GraphEdge(impl, iTransitive, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(iTransitive, iStart, "INJECTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "IStart");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(
            new[] { "ITransitive", "Impl" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_DiamondImplementsGraph_TerminatesWithNoDuplicates()
    {
        var store = new GraphStore();
        var iBase = new GraphNode("IBase", "Interface");
        var iDerived = new GraphNode("IDerived", "Interface");
        var impl = new GraphNode("Impl", "Class");
        store = store.WithEdge(new GraphEdge(iDerived, iBase, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(impl, iBase, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(impl, iDerived, "IMPLEMENTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "IBase");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        var ids = result!.AffectedIds.ToList();
        Assert.AreEqual(ids.Distinct().Count(), ids.Count, "No duplicate entries allowed.");
        CollectionAssert.AreEquivalent(new[] { "IDerived", "Impl" }, ids);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_OutboundBridgedInterfaceStaysOutOfResult_ImplementorsStayIn()
    {
        var store = new GraphStore();
        var start = new GraphNode("Start", "Class");
        var other = new GraphNode("Other", "Class");
        var iShared = new GraphNode("IShared", "Interface");
        store = store.WithEdge(new GraphEdge(start, iShared, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(other, iShared, "IMPLEMENTS"));

        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "Start");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(new[] { "Other" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphPathFind_UnknownSource_ReturnsNodeNotFoundError()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "UNKNOWN", "B");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.IsNull(error.FilePath);
    }

    [TestMethod]
    public async Task GraphPathFind_UnknownTarget_ReturnsNodeNotFoundError()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "A", "UNKNOWN");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("node_not_found", error!.Code);
        Assert.IsNull(error.FilePath);
    }

    [TestMethod]
    public async Task GraphPathFind_NoDirectedPath_ReturnsNoPathError()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "C", "A");

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("no_path", error!.Code);
    }

    [TestMethod]
    public async Task GraphPathFind_DirectPath_ReturnsCorrectSequence()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "A", "C");

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
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "A", "A");

        var path = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(path);
        Assert.AreEqual(1, path!.Count);
        Assert.AreEqual("A", path[0]);
    }

    [TestMethod]
    public async Task GraphPathFind_AdjacentNodes_ReturnsTwoElementPath()
    {
        var solutionPath = PersistGraph(BuildSampleStore());
        var tool = new GraphPathFindTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphPathFindAsync(solutionPath, "A", "B");

        var path = JsonSerializer.Deserialize<List<string>>(json, AnalysisJson.Options);
        Assert.IsNotNull(path);
        Assert.AreEqual(2, path!.Count);
        Assert.AreEqual("A", path[0]);
        Assert.AreEqual("B", path[1]);
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_BaseType_IncludesDerivedType()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(new GraphNode("Derived", "Class"), new GraphNode("Base", "Class"), "INHERITS"));
        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var json = await tool.GraphImpactAnalysisAsync(solutionPath, "Base");

        var result = JsonSerializer.Deserialize<GraphImpactResult>(json, AnalysisJson.Options);
        CollectionAssert.AreEqual(new[] { "Derived" }, result!.AffectedIds.ToList());
    }

    [TestMethod]
    public async Task GraphGetDependencies_NeighborWithDeclaringFile_ReturnsFile()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(new GraphNode("A", "Class", "/src/A.cs"), new GraphNode("B", "Class", "/src/B.cs"), "CALLS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("A", "Class", "/src/A.cs"), new GraphNode("NoFile", "Class"), "CALLS"));
        var solutionPath = PersistGraph(store);

        var json = await new GraphGetDependenciesTool(new PersistedGraphSynchronizer(new GraphRegistry())).GraphGetDependenciesAsync(solutionPath, "A");

        var neighbors = JsonSerializer.Deserialize<List<GraphNodeContract>>(json, AnalysisJson.Options)!;
        Assert.AreEqual("/src/B.cs", neighbors.Single(n => n.Id == "B").File);
        Assert.IsNull(neighbors.Single(n => n.Id == "NoFile").File);
        Assert.IsFalse(json.Contains("\"file\":null", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GraphImpactAnalysis_AffectedFiles_MapReturnedIdsOnly()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(new GraphNode("B", "Class", "/src/B.cs"), new GraphNode("C", "Class", "/src/C.cs"), "CALLS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("A", "Class", "/src/A.cs"), new GraphNode("B", "Class", "/src/B.cs"), "CALLS"));
        var solutionPath = PersistGraph(store);
        var tool = new GraphImpactAnalysisTool(new PersistedGraphSynchronizer(new GraphRegistry()));

        var impact = JsonSerializer.Deserialize<GraphImpactResult>(await tool.GraphImpactAnalysisAsync(solutionPath, "C"), AnalysisJson.Options)!;
        var none = await tool.GraphImpactAnalysisAsync(solutionPath, "A");

        CollectionAssert.AreEqual(new[] { "B", "A" }, impact.AffectedIds.ToList());
        CollectionAssert.AreEqual(new[] { "B", "A" }, impact.AffectedFiles!.Keys.ToList());
        Assert.AreEqual("/src/B.cs", impact.AffectedFiles["B"]);
        Assert.IsFalse(none.Contains("affectedFiles", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GraphQueryTools_SolutionWithoutGraphJson_BuildGraphAndReturnFiles()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var registry = new GraphRegistry();
        var synchronizer = CreateSynchronizer(registry);

        var dependencies = await new GraphGetDependenciesTool(synchronizer)
            .GraphGetDependenciesAsync(solutionPath, "IncrementalFixtures.Consumer.Client");
        var impact = await new GraphImpactAnalysisTool(synchronizer)
            .GraphImpactAnalysisAsync(solutionPath, "IncrementalFixtures.Core.Sender");
        var path = await new GraphPathFindTool(synchronizer)
            .GraphPathFindAsync(solutionPath, "IncrementalFixtures.Consumer.Client", "IncrementalFixtures.Core.Sender");

        Assert.IsTrue(File.Exists(GraphRegistry.GetGraphPath(solutionPath)));
        var sender = JsonSerializer.Deserialize<List<GraphNodeContract>>(dependencies, AnalysisJson.Options)!
            .Single(n => n.Id == "IncrementalFixtures.Core.Sender");
        Assert.AreEqual(Path.Combine(directory, "Core", "Sender.cs"), sender.File);
        var impactResult = JsonSerializer.Deserialize<GraphImpactResult>(impact, AnalysisJson.Options)!;
        CollectionAssert.Contains(impactResult.AffectedIds.ToList(), "IncrementalFixtures.Consumer.Client");
        Assert.AreEqual(Path.Combine(directory, "Consumer", "Client.cs"), impactResult.AffectedFiles!["IncrementalFixtures.Consumer.Client"]);
        CollectionAssert.AreEqual(new[] { "IncrementalFixtures.Consumer.Client", "IncrementalFixtures.Core.Sender" },
            JsonSerializer.Deserialize<List<string>>(path, AnalysisJson.Options));
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GraphGetDependencies_SourceChangedAfterScan_QueriesUpdatedGraph()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var registry = new GraphRegistry();
        var synchronizer = CreateSynchronizer(registry);
        var tool = new GraphGetDependenciesTool(synchronizer);
        var before = await tool.GraphGetDependenciesAsync(solutionPath, "IncrementalFixtures.Core.Sender");
        StringAssert.Contains(before, "IncrementalFixtures.Core.Audit");

        var sender = Path.Combine(directory, "Core", "Sender.cs");
        FixtureCopy.Edit(sender, "        Audit.Record();\n", "");
        var after = await tool.GraphGetDependenciesAsync(solutionPath, "IncrementalFixtures.Core.Sender");

        Assert.IsFalse(after.Contains("IncrementalFixtures.Core.Audit", StringComparison.Ordinal), after);
    }

    [TestMethod]
    public async Task GraphQueryTools_SynchronizedNoSolutionNoGraph_ReturnGraphNotFound()
    {
        var registry = new GraphRegistry();
        var synchronizer = CreateSynchronizer(registry);
        var solutionPath = Path.Combine(Path.GetTempPath(), $"context-manager-missing-{Guid.NewGuid():N}", "Missing.sln");

        var results = new[]
        {
            await new GraphGetDependenciesTool(synchronizer).GraphGetDependenciesAsync(solutionPath, "A"),
            await new GraphImpactAnalysisTool(synchronizer).GraphImpactAnalysisAsync(solutionPath, "A"),
            await new GraphPathFindTool(synchronizer).GraphPathFindAsync(solutionPath, "A", "B"),
        };

        foreach (var json in results)
            Assert.AreEqual("graph_not_found", JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options)?.Code);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GraphTools_CreatedFromServerRegistration_SynchronizeBeforeAnswering()
    {
        var directory = CopyFixture("GraphFixtures", "IncrementalFixtures");
        var solutionPath = Path.Combine(directory, "Incremental.sln");
        var services = Own(new ServiceCollection().AddContextManagerServices().BuildServiceProvider());

        var scan = await ActivatorUtilities.CreateInstance<ProjectScanTool>(services).ProjectScanAsync(solutionPath);
        FixtureCopy.Edit(Path.Combine(directory, "Core", "Sender.cs"), "        Audit.Record();\n", "");
        var dependencies = await ActivatorUtilities.CreateInstance<GraphGetDependenciesTool>(services)
            .GraphGetDependenciesAsync(solutionPath, "IncrementalFixtures.Core.Sender");
        var impact = await ActivatorUtilities.CreateInstance<GraphImpactAnalysisTool>(services)
            .GraphImpactAnalysisAsync(solutionPath, "IncrementalFixtures.Core.Audit");
        var path = await ActivatorUtilities.CreateInstance<GraphPathFindTool>(services)
            .GraphPathFindAsync(solutionPath, "IncrementalFixtures.Core.Sender", "IncrementalFixtures.Core.Audit");

        StringAssert.StartsWith(scan, "Scan partial.");
        Assert.IsFalse(dependencies.Contains("IncrementalFixtures.Core.Audit", StringComparison.Ordinal), dependencies);
        CollectionAssert.DoesNotContain(
            JsonSerializer.Deserialize<GraphImpactResult>(impact, AnalysisJson.Options)!.AffectedIds.ToList(),
            "IncrementalFixtures.Core.Sender");
        Assert.AreEqual("no_path", JsonSerializer.Deserialize<AnalysisError>(path, AnalysisJson.Options)?.Code, path);
    }

    private sealed class PersistedGraphSynchronizer(GraphRegistry registry) : IGraphSynchronizer
    {
        public Task<GraphSyncResult?> SynchronizeAsync(string solutionPath, bool reevaluate, CancellationToken ct = default)
        {
            var graph = registry.TryLoad(solutionPath)?.Capture();
            return Task.FromResult(graph is null
                ? null
                : new GraphSyncResult(graph, new GraphBuildResult("complete", 0, 0, 0, 0, 0, 0, 0, graph.NodeCount, graph.EdgeCount)));
        }
    }
}
