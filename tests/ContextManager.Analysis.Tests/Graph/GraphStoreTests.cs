using System.Text.Json;
using ContextManager.Analysis.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphStoreTests
{
    private static readonly GraphBaseline EmptyBaseline = new("/s.sln", [], [], [], false);

    private static GraphStore BuildSampleGraph()
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
    public void Node_IncreasesNodeCount()
    {
        var store = new GraphStore();
        store = store.WithNode(new GraphNode("X", "Class"));
        Assert.AreEqual(1, store.NodeCount);
    }

    [TestMethod]
    public void Node_DuplicateId_DoesNotIncreaseCount()
    {
        var store = new GraphStore();
        store = store.WithNode(new GraphNode("X", "Class"));
        store = store.WithNode(new GraphNode("X", "Interface"));
        Assert.AreEqual(1, store.NodeCount);
    }

    [TestMethod]
    public void Edge_IncreasesEdgeCount()
    {
        var store = BuildSampleGraph();
        Assert.AreEqual(4, store.EdgeCount);
    }

    [TestMethod]
    public void Edge_AutoAddsEndpoints_WhenNotPreAdded()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");

        store = store.WithEdge(new GraphEdge(a, b, "CALLS"));

        Assert.AreEqual(2, store.NodeCount);
        Assert.AreEqual(1, store.EdgeCount);
    }

    [TestMethod]
    public void TryGetNode_ExistingId_ReturnsTrue()
    {
        var store = BuildSampleGraph();
        var found = store.TryGetNode("A", out var node);

        Assert.IsTrue(found);
        Assert.IsNotNull(node);
        Assert.AreEqual("A", node!.Id);
        Assert.AreEqual("Class", node.Kind);
    }

    [TestMethod]
    public void TryGetNode_MissingId_ReturnsFalse()
    {
        var store = BuildSampleGraph();
        var found = store.TryGetNode("Z", out var node);

        Assert.IsFalse(found);
        Assert.IsNull(node);
    }

    [TestMethod]
    public void GraphNode_EqualityIsById_NotByKind()
    {
        var n1 = new GraphNode("A", "Class");
        var n2 = new GraphNode("A", "Interface");

        Assert.AreEqual(n1, n2);
        Assert.AreEqual(n1.GetHashCode(), n2.GetHashCode());
    }

    [TestMethod]
    public void GraphNode_DifferentId_NotEqual()
    {
        var n1 = new GraphNode("A", "Class");
        var n2 = new GraphNode("B", "Class");

        Assert.AreNotEqual(n1, n2);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_TypeNode_ReturnsOutEntriesWithEdgeKindCounts()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetAggregatedNeighbors("A");

        Assert.AreEqual(2, neighbors.Count);
        Assert.AreEqual("B", neighbors[0].Id);
        Assert.AreEqual("out", neighbors[0].Direction);
        Assert.AreEqual(1, neighbors[0].EdgeKinds["CALLS"]);
        Assert.AreEqual("D", neighbors[1].Id);
        Assert.AreEqual("out", neighbors[1].Direction);
        Assert.AreEqual(1, neighbors[1].EdgeKinds["INJECTS"]);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_StaticHelperConsumers_RolledUpToCallerTypesAsInEntries()
    {
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");
        var serviceB = new GraphNode("ServiceB", "Class");
        var serviceBProcess = new GraphNode("ServiceB.Process", "Method");

        store = store.WithEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceB, serviceBProcess, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));
        store = store.WithEdge(new GraphEdge(serviceBProcess, guardCheck, "CALLS"));

        var neighbors = store.GetAggregatedNeighbors("Guard");

        Assert.AreEqual(2, neighbors.Count);
        Assert.IsTrue(neighbors.All(n => n.Direction == "in"));
        Assert.IsTrue(neighbors.Any(n => n.Id == "ServiceA" && n.Kind == "Class" && n.EdgeKinds["CALLS"] == 1),
            "ServiceA must appear as an inbound CALLS consumer");
        Assert.IsTrue(neighbors.Any(n => n.Id == "ServiceB" && n.Kind == "Class" && n.EdgeKinds["CALLS"] == 1),
            "ServiceB must appear as an inbound CALLS consumer");
    }

    [TestMethod]
    public void GetAggregatedNeighbors_TypeNode_ExcludesOwnContainsMemberEdges()
    {
        var store = new GraphStore();
        var type = new GraphNode("Lonely", "Class");
        var m1 = new GraphNode("Lonely.A", "Method");
        var m2 = new GraphNode("Lonely.B", "Property");

        store = store.WithEdge(new GraphEdge(type, m1, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(type, m2, "CONTAINS"));

        var neighbors = store.GetAggregatedNeighbors("Lonely");

        Assert.AreEqual(0, neighbors.Count, "Own CONTAINS member edges must not appear");
    }

    [TestMethod]
    public void GetAggregatedNeighbors_SameCallerTypeViaMultipleMemberEdges_CountsAggregated()
    {
        var store = new GraphStore();
        var target = new GraphNode("Target", "Class");
        var targetA = new GraphNode("Target.A", "Method");
        var targetB = new GraphNode("Target.B", "Method");
        var caller = new GraphNode("Caller", "Class");
        var callerM1 = new GraphNode("Caller.M1", "Method");
        var callerM2 = new GraphNode("Caller.M2", "Method");

        store = store.WithEdge(new GraphEdge(target, targetA, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(target, targetB, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(caller, callerM1, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(caller, callerM2, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(callerM1, targetA, "CALLS"));
        store = store.WithEdge(new GraphEdge(callerM2, targetB, "CALLS"));

        var neighbors = store.GetAggregatedNeighbors("Target");

        Assert.AreEqual(1, neighbors.Count);
        Assert.AreEqual("Caller", neighbors[0].Id);
        Assert.AreEqual("in", neighbors[0].Direction);
        Assert.AreEqual(2, neighbors[0].EdgeKinds["CALLS"]);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_DualDirectionNeighbor_YieldsOneEntryPerDirection()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");

        store = store.WithEdge(new GraphEdge(a, b, "CALLS"));
        store = store.WithEdge(new GraphEdge(b, a, "REFERENCES"));

        var neighbors = store.GetAggregatedNeighbors("A");

        Assert.AreEqual(2, neighbors.Count);
        Assert.AreEqual("B", neighbors[0].Id);
        Assert.AreEqual("out", neighbors[0].Direction);
        Assert.AreEqual(1, neighbors[0].EdgeKinds["CALLS"]);
        Assert.AreEqual("B", neighbors[1].Id);
        Assert.AreEqual("in", neighbors[1].Direction);
        Assert.AreEqual(1, neighbors[1].EdgeKinds["REFERENCES"]);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_MemberNodeQuery_RollsUpNeighborsAndDropsDeclaringContains()
    {
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");

        store = store.WithEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));

        var neighbors = store.GetAggregatedNeighbors("Guard.Check");

        Assert.AreEqual(1, neighbors.Count);
        Assert.AreEqual("ServiceA", neighbors[0].Id);
        Assert.AreEqual("in", neighbors[0].Direction);
        Assert.AreEqual(1, neighbors[0].EdgeKinds["CALLS"]);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_UnknownNode_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetAggregatedNeighbors("Z");

        Assert.AreEqual(0, neighbors.Count);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_OutEntriesPrecedeInEntries()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetAggregatedNeighbors("B");

        Assert.AreEqual(3, neighbors.Count);
        Assert.AreEqual("C", neighbors[0].Id);
        Assert.AreEqual("out", neighbors[0].Direction);
        Assert.AreEqual("D", neighbors[1].Id);
        Assert.AreEqual("out", neighbors[1].Direction);
        Assert.AreEqual("A", neighbors[2].Id);
        Assert.AreEqual("in", neighbors[2].Direction);
        Assert.AreEqual(1, neighbors[2].EdgeKinds["CALLS"]);
    }

    [TestMethod]
    public void ShortestPath_DirectConnection_ReturnsTwoNodes()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("A", "B");

        CollectionAssert.AreEqual(new[] { "A", "B" }, path.ToList());
    }

    [TestMethod]
    public void ShortestPath_TwoHops_ReturnsThreeNodes()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("A", "C");

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, path.ToList());
    }

    [TestMethod]
    public void ShortestPath_SameSourceAndTarget_ReturnsSingleNode()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("A", "A");

        CollectionAssert.AreEqual(new[] { "A" }, path.ToList());
    }

    [TestMethod]
    public void ShortestPath_NoPathExists_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("C", "A");

        Assert.AreEqual(0, path.Count);
    }

    [TestMethod]
    public void ShortestPath_UnknownSource_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("Z", "A");

        Assert.AreEqual(0, path.Count);
    }

    [TestMethod]
    public void ShortestPath_UnknownTarget_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var path = store.ShortestPath("A", "Z");

        Assert.AreEqual(0, path.Count);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ShortestPath_SelfLoop_FindsTrivialPath()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        store = store.WithNode(a);
        store = store.WithEdge(new GraphEdge(a, a, "CALLS"));

        var path = store.ShortestPath("A", "A");

        CollectionAssert.AreEqual(new[] { "A" }, path.ToList());
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ShortestPath_CycleAlongTheWay_TerminatesAndReturnsCorrectPath()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        var c = new GraphNode("C", "Class");
        store = store.WithNode(a);
        store = store.WithNode(b);
        store = store.WithNode(c);
        store = store.WithEdge(new GraphEdge(a, b, "CALLS"));
        store = store.WithEdge(new GraphEdge(b, c, "CALLS"));
        store = store.WithEdge(new GraphEdge(b, a, "CALLS"));

        var path = store.ShortestPath("A", "C");

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, path.ToList());
    }

    [TestMethod]
    [Timeout(15_000)]
    public void ShortestPath_LargeCyclicGraph_TerminatesQuickly()
    {
        const int n = 5000;
        var nodes = Enumerable.Range(0, n).Select(i => new GraphNode($"N{i}", "Class")).ToList();
        var edges = nodes.SelectMany((src, i) => new[]
        {
            new GraphEdge(src, nodes[(i + 1) % n], "CALLS"),
            new GraphEdge(src, nodes[(i + 7) % n], "CALLS"),
        }).ToList();
        var store = new GraphStore().With(nodes, edges);

        var path = store.ShortestPath("N0", $"N{n - 1}");

        Assert.IsTrue(path.Count > 0, "Expected non-empty path.");
        Assert.AreEqual("N0", path[0]);
        Assert.AreEqual($"N{n - 1}", path[^1]);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void Edge_WithFreshNodeInstance_StillReachableViaInEdges()
    {
        var store = new GraphStore();
        store = store.WithNode(new GraphNode("A", "Class"));
        store = store.WithNode(new GraphNode("B", "Class"));

        store = store.WithEdge(new GraphEdge(new GraphNode("A", "Class"), new GraphNode("B", "Class"), "CALLS"));

        var result = store.ImpactBackward("B", new HashSet<string> { "CALLS" });

        CollectionAssert.AreEqual(new[] { "A" }, result.ToList());
    }

    [TestMethod]
    public void Serialize_ProducesNodesAndEdgesKeys()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        Assert.IsTrue(json.Contains("\"nodes\""), "JSON must contain 'nodes' key");
        Assert.IsTrue(json.Contains("\"edges\""), "JSON must contain 'edges' key");
    }

    [TestMethod]
    public void Serialize_ContainsAllNodes()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        foreach (var id in new[] { "A", "B", "C", "D" })
            Assert.IsTrue(json.Contains($"\"{id}\""), $"JSON must contain node id '{id}'");
    }

    [TestMethod]
    public void Deserialize_RoundTrip_PreservesNodeAndEdgeCount()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        var restored = new GraphStore();
        restored.Deserialize(json);

        Assert.AreEqual(store.NodeCount, restored.NodeCount);
        Assert.AreEqual(store.EdgeCount, restored.EdgeCount);
    }

    [TestMethod]
    public void Deserialize_RoundTrip_PreservesNodeKinds()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        var restored = new GraphStore();
        restored.Deserialize(json);

        Assert.IsTrue(restored.TryGetNode("D", out var d));
        Assert.AreEqual("Interface", d!.Kind);
    }

    [TestMethod]
    public void Deserialize_RoundTrip_GraphRemainsQueryable()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        var restored = new GraphStore();
        restored.Deserialize(json);

        var path = restored.ShortestPath("A", "C");
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, path.ToList());
    }

    [TestMethod]
    public void Deserialize_ReplacesExistingGraph()
    {
        var store = BuildSampleGraph();
        var json = store.Serialize();

        store = store.WithNode(new GraphNode("EXTRA", "Record"));

        store.Deserialize(json);

        Assert.AreEqual(4, store.NodeCount, "Deserialize must replace the graph, not merge");
        Assert.IsFalse(store.TryGetNode("EXTRA", out _));
    }

    [TestMethod]
    public void Deserialize_MalformedJson_ThrowsJsonException()
    {
        var store = new GraphStore();

        try
        {
            store.Deserialize("{ this is not valid json");
            Assert.Fail("Expected JsonException for malformed input.");
        }
        catch (JsonException)
        {
        }
    }

    [TestMethod]
    public void ImpactBackward_MemberCallsMember_RollsUpToCallerType()
    {
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var startMethod = new GraphNode("StartType.DoWork", "Method");
        var callerType = new GraphNode("CallerType", "Class");
        var callerMethod = new GraphNode("CallerType.Execute", "Method");

        store = store.WithEdge(new GraphEdge(startType, startMethod, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(callerType, callerMethod, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(callerMethod, startMethod, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };

        var impactResult = store.ImpactBackward("StartType", edgeTypes);
        CollectionAssert.AreEqual(new[] { "CallerType" }, impactResult.ToList());
    }

    [TestMethod]
    public void ImpactBackward_StaticUtility_ReturnsAllCallerTypes()
    {
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");
        var serviceB = new GraphNode("ServiceB", "Class");
        var serviceBProcess = new GraphNode("ServiceB.Process", "Method");

        store = store.WithEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceB, serviceBProcess, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));
        store = store.WithEdge(new GraphEdge(serviceBProcess, guardCheck, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Guard", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ServiceA", "ServiceB" }, result);
    }

    [TestMethod]
    public void ImpactBackward_ConcreteClass_ReachesInterfaceConsumers()
    {
        var store = new GraphStore();
        var concreteRepo = new GraphNode("ConcreteRepository", "Class");
        var iRepo = new GraphNode("IRepository", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store = store.WithEdge(new GraphEdge(concreteRepo, iRepo, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(controller, iRepo, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("ConcreteRepository", edgeTypes);

        CollectionAssert.AreEqual(new[] { "Controller" }, result.ToList());
    }

    [TestMethod]
    public void ImpactBackward_MultipleInterfaces_BridgesThroughAll()
    {
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iFoo = new GraphNode("IFoo", "Interface");
        var iBar = new GraphNode("IBar", "Interface");
        var consumerA = new GraphNode("ConsumerA", "Class");
        var consumerB = new GraphNode("ConsumerB", "Class");

        store = store.WithEdge(new GraphEdge(service, iFoo, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(service, iBar, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(consumerA, iFoo, "INJECTS"));
        store = store.WithEdge(new GraphEdge(consumerB, iBar, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Service", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ConsumerA", "ConsumerB" }, result);
    }

    [TestMethod]
    public void ImpactBackward_TransitiveChain_ControllerInjectsInterfaceImplementedByConcrete()
    {
        var store = new GraphStore();
        var outer = new GraphNode("Outer", "Class");
        var iOuter = new GraphNode("IOuter", "Interface");
        var controller = new GraphNode("Controller", "Class");
        var serviceB = new GraphNode("ServiceB", "Class");

        store = store.WithEdge(new GraphEdge(outer, iOuter, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(controller, iOuter, "INJECTS"));
        store = store.WithEdge(new GraphEdge(serviceB, outer, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Outer", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "Controller", "ServiceB" }, result);
        CollectionAssert.DoesNotContain(result, "IOuter");
        CollectionAssert.DoesNotContain(result, "Outer");
    }

    [TestMethod]
    public void ImpactBackward_ReferencesAndReturnsEdges_RolledUpToType()
    {
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var userType = new GraphNode("UserType", "Class");
        var userProp = new GraphNode("UserType.Prop", "Property");
        var providerType = new GraphNode("ProviderType", "Class");
        var providerGet = new GraphNode("ProviderType.Get", "Method");

        store = store.WithNode(startType);
        store = store.WithEdge(new GraphEdge(userType, userProp, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(userProp, startType, "REFERENCES"));
        store = store.WithEdge(new GraphEdge(providerType, providerGet, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(providerGet, startType, "RETURNS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("StartType", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ProviderType", "UserType" }, result);
    }

    [TestMethod]
    public void ImpactBackward_SeedNodes_ExcludedFromResult()
    {
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var startM = new GraphNode("StartType.M", "Method");
        var iFace = new GraphNode("IFace", "Interface");
        var ifaceContract = new GraphNode("IFace.Contract", "Method");
        var callerType = new GraphNode("CallerType", "Class");
        var callerUse = new GraphNode("CallerType.Use", "Method");

        store = store.WithEdge(new GraphEdge(startType, startM, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(startType, iFace, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(iFace, ifaceContract, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(callerType, callerUse, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(callerUse, startM, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("StartType", edgeTypes);

        CollectionAssert.DoesNotContain(result.ToList(), "StartType");
        CollectionAssert.DoesNotContain(result.ToList(), "StartType.M");
        CollectionAssert.DoesNotContain(result.ToList(), "IFace");
        CollectionAssert.DoesNotContain(result.ToList(), "IFace.Contract");
        CollectionAssert.AreEqual(new[] { "CallerType" }, result.ToList());
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ImpactBackward_SelfReferential_Terminates()
    {
        var store = new GraphStore();
        var t = new GraphNode("RecursiveType", "Class");
        var m = new GraphNode("RecursiveType.Recurse", "Method");

        store = store.WithEdge(new GraphEdge(t, m, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(m, m, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("RecursiveType", edgeTypes);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ImpactBackward_CyclicTypes_TerminatesAndDeduplicates()
    {
        var store = new GraphStore();
        var typeA = new GraphNode("TypeA", "Class");
        var typeAM = new GraphNode("TypeA.M", "Method");
        var typeB = new GraphNode("TypeB", "Class");
        var typeBM = new GraphNode("TypeB.M", "Method");

        store = store.WithEdge(new GraphEdge(typeA, typeAM, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(typeB, typeBM, "CONTAINS"));
        store = store.WithEdge(new GraphEdge(typeAM, typeBM, "CALLS"));
        store = store.WithEdge(new GraphEdge(typeBM, typeAM, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("TypeA", edgeTypes);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("TypeB", result[0]);
    }

    [TestMethod]
    public void ImpactBackward_UnknownStartNode_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var result = store.ImpactBackward("NonExistent", new HashSet<string> { "CALLS" });

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ImpactBackward_TwoHopCrossLayer_ServiceSeed_ReachesController()
    {
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iService = new GraphNode("IService", "Interface");
        var orchestrator = new GraphNode("Orchestrator", "Class");
        var iOrchestrator = new GraphNode("IOrchestrator", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store = store.WithEdge(new GraphEdge(service, iService, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(orchestrator, iOrchestrator, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(orchestrator, iService, "INJECTS"));
        store = store.WithEdge(new GraphEdge(controller, iOrchestrator, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Service", edgeTypes);

        CollectionAssert.Contains(result.ToList(), "Orchestrator");
        CollectionAssert.Contains(result.ToList(), "Controller");
        CollectionAssert.DoesNotContain(result.ToList(), "Service");
        CollectionAssert.DoesNotContain(result.ToList(), "IService");
        CollectionAssert.DoesNotContain(result.ToList(), "IOrchestrator");
    }

    [TestMethod]
    public void ImpactBackward_TwoHopCrossLayer_IServiceSeed_ReachesController()
    {
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iService = new GraphNode("IService", "Interface");
        var orchestrator = new GraphNode("Orchestrator", "Class");
        var iOrchestrator = new GraphNode("IOrchestrator", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store = store.WithEdge(new GraphEdge(service, iService, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(orchestrator, iOrchestrator, "IMPLEMENTS"));
        store = store.WithEdge(new GraphEdge(orchestrator, iService, "INJECTS"));
        store = store.WithEdge(new GraphEdge(controller, iOrchestrator, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("IService", edgeTypes);

        CollectionAssert.Contains(result.ToList(), "Orchestrator");
        CollectionAssert.Contains(result.ToList(), "Controller");
        CollectionAssert.DoesNotContain(result.ToList(), "IService");
        CollectionAssert.DoesNotContain(result.ToList(), "IOrchestrator");
    }

    [TestMethod]
    [Timeout(15_000)]
    public void ConcurrentReaders_AmidRebuild_NeverThrowAndObserveCommittedSnapshot()
    {
        var store = BuildSampleGraph();

        Exception? readerException = null;
        var writerDone = new ManualResetEventSlim(false);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                var spin = new SpinWait();
                while (!writerDone.IsSet)
                {
                    store.GetAggregatedNeighbors("A");
                    store.ShortestPath("A", "C");
                    _ = store.NodeCount;
                    spin.SpinOnce();
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            var c = new GraphNode("C", "Class");
            var edges = new List<GraphEdge>
            {
                new(new GraphNode("A", "Class"), new GraphNode("B", "Class"), "CALLS"),
                new(new GraphNode("B", "Class"), c, "CALLS"),
            };
            edges.AddRange(Enumerable.Range(0, 300).Select(i => new GraphEdge(c, new GraphNode($"N{i}", "Class"), "CALLS")));
            var record = new GraphDocumentRecord("P", "/doc.cs", "checksum", 0, 0, [], edges);

            var candidate = new GraphStore().Stage([], [record], EmptyBaseline);
            store.Publish(candidate);
            writerDone.Set();
        });

        Task.WaitAll(readers);
        writer.Wait();

        Assert.IsNull(readerException,
            $"Readers must not throw during a concurrent rebuild. Got: {readerException}");

        Assert.IsTrue(store.NodeCount >= 300, "Published candidate must be visible after Publish.");
        var neighbors = store.GetAggregatedNeighbors("A");
        Assert.IsTrue(neighbors.Any(n => n.Id == "B"),
            "Post-commit read must see the rebuilt graph (A → B).");
    }

    [TestMethod]
    [Timeout(10_000)]
    public void ImpactBackward_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var store = new GraphStore();
        const int n = 60;
        for (var i = 0; i < n; i++)
        {
            store = store.WithNode(new GraphNode($"T{i}", "Class"));
            store = store.WithEdge(new GraphEdge(
                new GraphNode($"T{i}", "Class"),
                new GraphNode($"T{i}.M", "Method"),
                "CONTAINS"));
        }
        for (var i = 0; i < n - 1; i++)
        {
            store = store.WithEdge(new GraphEdge(
                new GraphNode($"T{i + 1}.M", "Method"),
                new GraphNode($"T{i}.M", "Method"),
                "CALLS"));
        }

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            store.ImpactBackward("T0", edgeTypes, cts.Token);
            Assert.Fail("Expected OperationCanceledException for a pre-cancelled token.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public void Edge_PreservesDifferentKindsAndDeduplicatesTriplesAcrossRoundTrip()
    {
        var store = new GraphStore();
        var source = new GraphNode("Source", "Class");
        var target = new GraphNode("Target", "Class");

        store = store.WithEdge(new GraphEdge(source, target, "INJECTS"));
        store = store.WithEdge(new GraphEdge(source, target, "REFERENCES"));
        store = store.WithEdge(new GraphEdge(source, target, "INJECTS"));

        Assert.AreEqual(2, store.EdgeCount);
        var restored = new GraphStore();
        restored.Deserialize(store.Serialize());
        Assert.AreEqual(2, restored.EdgeCount);
        var neighbor = restored.GetAggregatedNeighbors("Source").Single();
        Assert.AreEqual(1, neighbor.EdgeKinds["INJECTS"]);
        Assert.AreEqual(1, neighbor.EdgeKinds["REFERENCES"]);
    }

    [TestMethod]
    public void ImpactBackward_MemberConsumerContinuesThroughDeclaringTypeAndInheritance()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(new GraphNode("Utility", "Class"), new GraphNode("Utility.Run", "Method"), "CONTAINS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("Service", "Class"), new GraphNode("Service.Run", "Method"), "CONTAINS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("Service.Run", "Method"), new GraphNode("Utility.Run", "Method"), "CALLS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("Controller", "Class"), new GraphNode("Service", "Class"), "INJECTS"));
        store = store.WithEdge(new GraphEdge(new GraphNode("Derived", "Class"), new GraphNode("Utility", "Class"), "INHERITS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS", "INHERITS" };
        var result = store.ImpactBackward("Utility", edgeTypes);

        CollectionAssert.Contains(result.ToList(), "Service");
        CollectionAssert.Contains(result.ToList(), "Controller");
        CollectionAssert.Contains(result.ToList(), "Derived");
    }

    [TestMethod]
    public void ShortestPath_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var store = BuildSampleGraph();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => store.ShortestPath("A", "C", cts.Token));
    }

    [TestMethod]
    public void GetAggregatedNeighbors_HighFanIn_UsesOwnerIndexAndPreservesEncounterOrder()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(new GraphNode("Target", "Class"), new GraphNode("Target.Run", "Method"), "CONTAINS"));

        for (var i = 0; i < 1_000; i++)
        {
            var owner = new GraphNode($"Consumer{i}", "Class");
            var member = new GraphNode($"Consumer{i}.Run", "Method");
            store = store.WithEdge(new GraphEdge(owner, member, "CONTAINS"));
            store = store.WithEdge(new GraphEdge(member, new GraphNode("Target.Run", "Method"), "CALLS"));
        }

        var neighbors = store.GetAggregatedNeighbors("Target");

        Assert.AreEqual(1_000, neighbors.Count);
        Assert.AreEqual("Consumer0", neighbors[0].Id);
        Assert.AreEqual("Consumer999", neighbors[^1].Id);
    }

    private static GraphNode N(string id, string kind = "Class", string? file = null) => new(id, kind, file);

    private static GraphDocumentRecord Doc(string path, int order, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges) =>
        new("P", path, $"sum-{path}", 0, order, nodes, edges);

    private static string GraphOnly(GraphStore store)
    {
        using var json = JsonDocument.Parse(store.Serialize());
        return json.RootElement.GetProperty("documents").GetRawText();
    }

    [TestMethod]
    public void Serialize_NodeFile_RoundTripsAndReachesNeighbors()
    {
        var store = new GraphStore();
        store = store.WithEdge(new GraphEdge(N("A", file: "/src/A.cs"), N("B", file: "/src/B.cs"), "CALLS"));

        var restored = new GraphStore();
        restored.Deserialize(store.Serialize());

        Assert.IsTrue(restored.TryGetNode("B", out var b));
        Assert.AreEqual("/src/B.cs", b!.File);
        Assert.AreEqual("/src/B.cs", restored.GetAggregatedNeighbors("A").Single().File);
    }

    [TestMethod]
    public void Deserialize_PayloadWithoutDocuments_LoadsEmptyGraph()
    {
        const string json = """{"nodes":[{"id":"A","kind":"Class"},{"id":"B","kind":"Class"}],"edges":[{"source":"A","target":"B","type":"CALLS"}]}""";
        var store = BuildSampleGraph();

        store.Deserialize(json);

        Assert.AreEqual(0, store.NodeCount);
        Assert.IsNull(store.Revision);
        Assert.IsNull(store.Baseline);
    }

    [TestMethod]
    public void Stage_WithDocuments_RoundTripsProvenanceRevisionAndBaseline()
    {
        var store = new GraphStore();
        var baseline = new GraphBaseline("/s.sln", [], [new GraphTrackedFile("/s.sln", "abc")], ["/a.cs"], true);
        var candidate = new GraphStore().Stage([], [Doc("/a.cs", 0, [N("A")], [new GraphEdge(N("A"), N("B"), "CALLS")])], baseline);

        var restored = new GraphStore();
        restored.Deserialize(candidate.Serialize());

        Assert.IsTrue(restored.Baseline!.HasWorkspaceFailures);
        Assert.AreEqual(candidate.Revision, restored.Revision);
        Assert.AreEqual("abc", restored.Baseline!.Inputs.Single().Checksum);
        Assert.AreEqual(1, restored.Documents.Count);
        Assert.AreEqual(candidate.Serialize(), restored.Serialize());
    }

    [TestMethod]
    public void Stage_DoesNotPublishUntilPublish()
    {
        var store = BuildSampleGraph();

        var candidate = new GraphStore().Stage([], [Doc("/x.cs", 0, [N("X")], [])], EmptyBaseline);

        Assert.IsTrue(store.TryGetNode("A", out _));
        Assert.IsFalse(store.TryGetNode("X", out _));
        store.Publish(candidate);
        Assert.IsFalse(store.TryGetNode("A", out _));
        Assert.IsTrue(store.TryGetNode("X", out _));
    }

    [TestMethod]
    public void Stage_RemovedDocument_EvictsOnlyUnownedNodesAndEdges()
    {
        var store = new GraphStore();
        var shared = new GraphEdge(N("Caller"), N("Target"), "CALLS");
        store.Publish(new GraphStore().Stage([], [
            Doc("/caller.cs", 0, [N("Caller")], [shared, new GraphEdge(N("Caller"), N("Only"), "REFERENCES")]),
            Doc("/target.cs", 1, [N("Target")], []),
            Doc("/other.cs", 2, [N("Other")], [shared]),
        ], EmptyBaseline));

        var updated = store.Stage([("P", "/caller.cs")], [], EmptyBaseline);

        Assert.IsFalse(updated.TryGetNode("Only", out _), "A node owned only by the removed document disappears.");
        Assert.IsTrue(updated.TryGetNode("Caller", out _), "A node still referenced by a surviving contribution stays.");
        Assert.IsTrue(updated.TryGetNode("Target", out _));
        Assert.AreEqual(1, updated.EdgeCount, "The shared edge survives through the other document's ownership.");
    }

    [TestMethod]
    public void Stage_PartialTypeAcrossDocuments_SurvivesRemovalOfOneDeclarationAndUsesSurvivingFile()
    {
        var store = new GraphStore();
        var part1 = Doc("/T.Part1.cs", 0, [N("T", file: "/T.Part1.cs"), N("T.A", "Method", "/T.Part1.cs")],
            [new GraphEdge(N("T", file: "/T.Part1.cs"), N("T.A", "Method", "/T.Part1.cs"), "CONTAINS")]);
        var part2 = Doc("/T.Part2.cs", 1, [N("T", file: "/T.Part2.cs"), N("T.B", "Method", "/T.Part2.cs")],
            [new GraphEdge(N("T", file: "/T.Part2.cs"), N("T.B", "Method", "/T.Part2.cs"), "CONTAINS")]);
        store.Publish(new GraphStore().Stage([], [part1, part2], EmptyBaseline));

        var updated = store.Stage([part1.Key], [], EmptyBaseline);

        Assert.IsTrue(updated.TryGetNode("T", out var t));
        Assert.AreEqual("/T.Part2.cs", t!.File);
        Assert.IsFalse(updated.TryGetNode("T.A", out _));
        Assert.IsTrue(updated.TryGetNode("T.B", out _));
        Assert.AreEqual(0, updated.GetAggregatedNeighbors("T").Count);
    }

    [TestMethod]
    public void Stage_AfterRoundTripAndOwnerRemoval_PreservesSurvivingDeclarationAndEndpointMetadata()
    {
        var first = Doc("/First.cs", 0, [N("Shared", "Class", "/First.cs")],
            [new GraphEdge(N("Caller", file: "/First.cs"), N("Target", "Class", "/First.cs"), "REFERENCES")]);
        var second = Doc("/Second.cs", 1, [N("Shared", "Interface", "/Second.cs")],
            [new GraphEdge(N("Caller", file: "/Second.cs"), N("Target", "Interface", "/Second.cs"), "REFERENCES")]);
        var original = new GraphStore().Stage([], [first, second], EmptyBaseline);
        var restored = new GraphStore();
        restored.Deserialize(original.Serialize());

        var updated = restored.Stage([first.Key], [], EmptyBaseline);
        var fresh = new GraphStore().Stage([], [second], EmptyBaseline);

        Assert.AreEqual(GraphOnly(fresh), GraphOnly(updated));
        Assert.IsTrue(updated.TryGetNode("Shared", out var declaration));
        Assert.AreEqual("/Second.cs", declaration!.File);
        Assert.AreEqual("Interface", declaration.Kind);
        Assert.IsTrue(updated.TryGetNode("Target", out var endpoint));
        Assert.AreEqual("/Second.cs", endpoint!.File);
        Assert.AreEqual("Interface", endpoint.Kind);
    }

    [TestMethod]
    public void Stage_ReplacingEarlyDocument_KeepsFreshScanEncounterOrder()
    {
        var first = Doc("/1.cs", 0, [N("A")], [new GraphEdge(N("A"), N("Hub"), "CALLS")]);
        var second = Doc("/2.cs", 1, [N("B")], [new GraphEdge(N("B"), N("Hub"), "CALLS")]);
        var replacement = Doc("/1.cs", 0, [N("A2")], [new GraphEdge(N("A2"), N("Hub"), "CALLS"), new GraphEdge(N("A2"), N("B"), "CALLS")]);
        var store = new GraphStore();
        store.Publish(new GraphStore().Stage([], [first, second], EmptyBaseline));

        var incremental = store.Stage([], [replacement], EmptyBaseline);
        var fresh = new GraphStore().Stage([], [replacement, second], EmptyBaseline);

        Assert.AreEqual(GraphOnly(fresh), GraphOnly(incremental));
        CollectionAssert.AreEqual(
            fresh.GetAggregatedNeighbors("Hub").Select(n => n.Id).ToList(),
            incremental.GetAggregatedNeighbors("Hub").Select(n => n.Id).ToList());
        Assert.AreEqual("A2", incremental.GetAggregatedNeighbors("Hub")[0].Id);
    }

    [TestMethod]
    public void Stage_InsertingDocumentBeforeExistingOnes_MatchesFreshScan()
    {
        var a = Doc("/A.cs", 0, [N("A")], []);
        var b = Doc("/B.cs", 1, [N("B")], [new GraphEdge(N("B"), N("A"), "REFERENCES")]);
        var inserted = Doc("/Inserted.cs", 1, [N("Inserted")], [new GraphEdge(N("Inserted"), N("A"), "CALLS")]);
        var shifted = b with { DocumentOrder = 2 };
        var store = new GraphStore();
        store.Publish(new GraphStore().Stage([], [a, b], EmptyBaseline));

        var incremental = store.Stage([], [inserted, shifted], EmptyBaseline);
        var fresh = new GraphStore().Stage([], [a, inserted, shifted], EmptyBaseline);

        Assert.AreEqual(GraphOnly(fresh), GraphOnly(incremental));
        Assert.AreEqual(fresh.NodeCount, incremental.NodeCount);
        Assert.AreEqual(fresh.EdgeCount, incremental.EdgeCount);
        CollectionAssert.AreEqual(
            fresh.GetAggregatedNeighbors("A").Select(n => n.Id).ToList(),
            incremental.GetAggregatedNeighbors("A").Select(n => n.Id).ToList());
    }

    [TestMethod]
    public void Capture_KeepsItsSnapshotAfterLaterPublish()
    {
        var store = BuildSampleGraph();
        var captured = store.Capture();

        store.Publish(new GraphStore().Stage([], [Doc("/x.cs", 0, [N("X")], [])], EmptyBaseline));

        Assert.IsTrue(captured.TryGetNode("A", out _));
        Assert.IsFalse(captured.TryGetNode("X", out _));
    }
}
