using System.Text.Json;
using ContextManager.Analysis.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

/// <summary>
/// Tests for GraphStore: add/query, BFS backward, shortest path, and serialization round-trip.
/// All graphs are built by hand — no Roslyn involved.
/// </summary>
[TestClass]
public class GraphStoreTests
{
    // Hand-built graph used by most tests:
    //
    //   A --CALLS--> B --CALLS--> C
    //   A --INJECTS--> D
    //   B --INJECTS--> D
    //
    // Node kinds: Class for all.

    private static GraphStore BuildSampleGraph()
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

    // ── AddNode / AddEdge ────────────────────────────────────────────────────

    [TestMethod]
    public void AddNode_IncreasesNodeCount()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("X", "Class"));
        Assert.AreEqual(1, store.NodeCount);
    }

    [TestMethod]
    public void AddNode_DuplicateId_DoesNotIncreaseCount()
    {
        var store = new GraphStore();
        store.AddNode(new GraphNode("X", "Class"));
        store.AddNode(new GraphNode("X", "Interface")); // same Id, different Kind
        Assert.AreEqual(1, store.NodeCount);
    }

    [TestMethod]
    public void AddEdge_IncreasesEdgeCount()
    {
        var store = BuildSampleGraph();
        Assert.AreEqual(4, store.EdgeCount);
    }

    [TestMethod]
    public void AddEdge_AutoAddsEndpoints_WhenNotPreAdded()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");

        store.AddEdge(new GraphEdge(a, b, "CALLS"));

        Assert.AreEqual(2, store.NodeCount);
        Assert.AreEqual(1, store.EdgeCount);
    }

    // ── TryGetNode ───────────────────────────────────────────────────────────

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

    // ── GraphNode equality ───────────────────────────────────────────────────

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

    // ── GetAggregatedNeighbors ───────────────────────────────────────────────

    [TestMethod]
    public void GetAggregatedNeighbors_TypeNode_ReturnsOutEntriesWithEdgeKindCounts()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetAggregatedNeighbors("A");

        // A -> B (CALLS, out), A -> D (INJECTS, out) — out-entries in graph order
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
        // Guard --CONTAINS--> Guard.Check
        // ServiceA --CONTAINS--> ServiceA.Validate
        // ServiceB --CONTAINS--> ServiceB.Process
        // ServiceA.Validate --CALLS--> Guard.Check
        // ServiceB.Process --CALLS--> Guard.Check
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");
        var serviceB = new GraphNode("ServiceB", "Class");
        var serviceBProcess = new GraphNode("ServiceB.Process", "Method");

        store.AddEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceB, serviceBProcess, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));
        store.AddEdge(new GraphEdge(serviceBProcess, guardCheck, "CALLS"));

        var neighbors = store.GetAggregatedNeighbors("Guard");

        // The two consumer types surface as in-entries rolled up from their method nodes.
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
        // Type with members but no external dependencies → empty result, no CONTAINS noise.
        var store = new GraphStore();
        var type = new GraphNode("Lonely", "Class");
        var m1 = new GraphNode("Lonely.A", "Method");
        var m2 = new GraphNode("Lonely.B", "Property");

        store.AddEdge(new GraphEdge(type, m1, "CONTAINS"));
        store.AddEdge(new GraphEdge(type, m2, "CONTAINS"));

        var neighbors = store.GetAggregatedNeighbors("Lonely");

        Assert.AreEqual(0, neighbors.Count, "Own CONTAINS member edges must not appear");
    }

    [TestMethod]
    public void GetAggregatedNeighbors_SameCallerTypeViaMultipleMemberEdges_CountsAggregated()
    {
        // Caller.M1 --CALLS--> Target.A and Caller.M2 --CALLS--> Target.B
        // → ONE in-entry { Caller, CALLS: 2 } on the Target type query.
        var store = new GraphStore();
        var target = new GraphNode("Target", "Class");
        var targetA = new GraphNode("Target.A", "Method");
        var targetB = new GraphNode("Target.B", "Method");
        var caller = new GraphNode("Caller", "Class");
        var callerM1 = new GraphNode("Caller.M1", "Method");
        var callerM2 = new GraphNode("Caller.M2", "Method");

        store.AddEdge(new GraphEdge(target, targetA, "CONTAINS"));
        store.AddEdge(new GraphEdge(target, targetB, "CONTAINS"));
        store.AddEdge(new GraphEdge(caller, callerM1, "CONTAINS"));
        store.AddEdge(new GraphEdge(caller, callerM2, "CONTAINS"));
        store.AddEdge(new GraphEdge(callerM1, targetA, "CALLS"));
        store.AddEdge(new GraphEdge(callerM2, targetB, "CALLS"));

        var neighbors = store.GetAggregatedNeighbors("Target");

        Assert.AreEqual(1, neighbors.Count);
        Assert.AreEqual("Caller", neighbors[0].Id);
        Assert.AreEqual("in", neighbors[0].Direction);
        Assert.AreEqual(2, neighbors[0].EdgeKinds["CALLS"]);
    }

    [TestMethod]
    public void GetAggregatedNeighbors_DualDirectionNeighbor_YieldsOneEntryPerDirection()
    {
        // A --CALLS--> B and B --REFERENCES--> A → for A: out {B, CALLS} then in {B, REFERENCES}.
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");

        store.AddEdge(new GraphEdge(a, b, "CALLS"));
        store.AddEdge(new GraphEdge(b, a, "REFERENCES"));

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
        // Querying Guard.Check directly: callers roll up to their types; the inbound
        // CONTAINS edge from Guard (its declaring type) is structural noise and excluded.
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");

        store.AddEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));

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

        // Out: C (CALLS), D (INJECTS) — In: A (CALLS)
        Assert.AreEqual(3, neighbors.Count);
        Assert.AreEqual("C", neighbors[0].Id);
        Assert.AreEqual("out", neighbors[0].Direction);
        Assert.AreEqual("D", neighbors[1].Id);
        Assert.AreEqual("out", neighbors[1].Direction);
        Assert.AreEqual("A", neighbors[2].Id);
        Assert.AreEqual("in", neighbors[2].Direction);
        Assert.AreEqual(1, neighbors[2].EdgeKinds["CALLS"]);
    }

    // ── BfsBackward ──────────────────────────────────────────────────────────

    [TestMethod]
    public void BfsBackward_FromC_FollowingCalls_ReturnsBThenA()
    {
        var store = BuildSampleGraph();
        // C <- B (CALLS) <- A (CALLS)
        var result = store.BfsBackward("C", new HashSet<string> { "CALLS" });

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("B", result[0]); // BFS level 1
        Assert.AreEqual("A", result[1]); // BFS level 2
    }

    [TestMethod]
    public void BfsBackward_FromD_FollowingInjects_ReturnsBothCallers()
    {
        var store = BuildSampleGraph();
        // D <- A (INJECTS), D <- B (INJECTS)
        var result = store.BfsBackward("D", new HashSet<string> { "INJECTS" }).Order().ToList();

        CollectionAssert.AreEqual(new[] { "A", "B" }, result);
    }

    [TestMethod]
    public void BfsBackward_EdgeTypeNotMatching_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        // No INHERITS edges in the graph
        var result = store.BfsBackward("C", new HashSet<string> { "INHERITS" });

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void BfsBackward_UnknownStartNode_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var result = store.BfsBackward("Z", new HashSet<string> { "CALLS" });

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void BfsBackward_DoesNotIncludeStartNode()
    {
        var store = BuildSampleGraph();
        var result = store.BfsBackward("C", new HashSet<string> { "CALLS" });

        Assert.IsFalse(result.Contains("C"), "Start node must not appear in result");
    }

    // ── ShortestPath ─────────────────────────────────────────────────────────

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
        // C has no out-edges, so C -> A is impossible
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

    // ── Cycles: BfsBackward + ShortestPath ──────────────────────────────────
    // Regression tests for "infinite loop" reports on cyclic graphs.

    [TestMethod]
    [Timeout(5_000)]
    public void BfsBackward_SelfLoop_DoesNotLoop()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        store.AddNode(a);
        store.AddEdge(new GraphEdge(a, a, "CALLS"));

        var result = store.BfsBackward("A", new HashSet<string> { "CALLS" });

        Assert.AreEqual(0, result.Count, "Self-loop must not include A in its own backward set.");
    }

    [TestMethod]
    [Timeout(5_000)]
    public void BfsBackward_TwoNodeCycle_TerminatesAndReturnsOtherNode()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        store.AddNode(a);
        store.AddNode(b);
        store.AddEdge(new GraphEdge(a, b, "CALLS"));
        store.AddEdge(new GraphEdge(b, a, "CALLS"));

        var result = store.BfsBackward("A", new HashSet<string> { "CALLS" });

        CollectionAssert.AreEqual(new[] { "B" }, result.ToList());
    }

    [TestMethod]
    [Timeout(5_000)]
    public void BfsBackward_ThreeNodeCycle_VisitsEachOnce()
    {
        // A -> B -> C -> A
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        var c = new GraphNode("C", "Class");
        store.AddNode(a);
        store.AddNode(b);
        store.AddNode(c);
        store.AddEdge(new GraphEdge(a, b, "CALLS"));
        store.AddEdge(new GraphEdge(b, c, "CALLS"));
        store.AddEdge(new GraphEdge(c, a, "CALLS"));

        var result = store.BfsBackward("A", new HashSet<string> { "CALLS" }).Order().ToList();

        CollectionAssert.AreEqual(new[] { "B", "C" }, result);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ShortestPath_SelfLoop_FindsTrivialPath()
    {
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        store.AddNode(a);
        store.AddEdge(new GraphEdge(a, a, "CALLS"));

        var path = store.ShortestPath("A", "A");

        CollectionAssert.AreEqual(new[] { "A" }, path.ToList());
    }

    [TestMethod]
    [Timeout(5_000)]
    public void ShortestPath_CycleAlongTheWay_TerminatesAndReturnsCorrectPath()
    {
        // A -> B -> C, and B -> A creating a cycle.
        var store = new GraphStore();
        var a = new GraphNode("A", "Class");
        var b = new GraphNode("B", "Class");
        var c = new GraphNode("C", "Class");
        store.AddNode(a);
        store.AddNode(b);
        store.AddNode(c);
        store.AddEdge(new GraphEdge(a, b, "CALLS"));
        store.AddEdge(new GraphEdge(b, c, "CALLS"));
        store.AddEdge(new GraphEdge(b, a, "CALLS"));

        var path = store.ShortestPath("A", "C");

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, path.ToList());
    }

    // ── Scale + duplicate-instance edges (regression hypotheses for "infinite loop") ──

    [TestMethod]
    [Timeout(15_000)]
    public void BfsBackward_LargeCyclicGraph_TerminatesQuickly()
    {
        // 5000 nodes in a giant SCC: each node points to the next + cross-edges back.
        var store = new GraphStore();
        const int n = 5000;
        for (var i = 0; i < n; i++)
            store.AddNode(new GraphNode($"N{i}", "Class"));

        for (var i = 0; i < n; i++)
        {
            var src = new GraphNode($"N{i}", "Class");
            var nextId = (i + 1) % n;
            var crossId = (i + 7) % n; // cross-edge to introduce many cycles
            store.AddEdge(new GraphEdge(src, new GraphNode($"N{nextId}", "Class"), "CALLS"));
            store.AddEdge(new GraphEdge(src, new GraphNode($"N{crossId}", "Class"), "CALLS"));
        }

        var result = store.BfsBackward("N0", new HashSet<string> { "CALLS" });

        // Whole graph is strongly connected → every other node reaches N0.
        Assert.AreEqual(n - 1, result.Count, $"Expected {n - 1} ancestors, got {result.Count}");
    }

    [TestMethod]
    [Timeout(15_000)]
    public void ShortestPath_LargeCyclicGraph_TerminatesQuickly()
    {
        var store = new GraphStore();
        const int n = 5000;
        for (var i = 0; i < n; i++)
            store.AddNode(new GraphNode($"N{i}", "Class"));

        for (var i = 0; i < n; i++)
        {
            var src = new GraphNode($"N{i}", "Class");
            store.AddEdge(new GraphEdge(src, new GraphNode($"N{(i + 1) % n}", "Class"), "CALLS"));
            store.AddEdge(new GraphEdge(src, new GraphNode($"N{(i + 7) % n}", "Class"), "CALLS"));
        }

        var path = store.ShortestPath("N0", $"N{n - 1}");

        Assert.IsTrue(path.Count > 0, "Expected non-empty path.");
        Assert.AreEqual("N0", path[0]);
        Assert.AreEqual($"N{n - 1}", path[^1]);
    }

    [TestMethod]
    [Timeout(5_000)]
    public void AddEdge_WithFreshNodeInstance_StillReachableViaInEdges()
    {
        // Reproduces EdgeExtractor's pattern: it creates `new GraphNode(...)` per edge
        // instead of reusing the instance stored in _graph. This test guards that
        // BidirectionalGraph treats them as equal via GraphNode.Equals(Id).
        var store = new GraphStore();
        store.AddNode(new GraphNode("A", "Class"));
        store.AddNode(new GraphNode("B", "Class"));

        // Note: brand-new instances, not the ones added above.
        store.AddEdge(new GraphEdge(new GraphNode("A", "Class"), new GraphNode("B", "Class"), "CALLS"));

        var result = store.BfsBackward("B", new HashSet<string> { "CALLS" });

        CollectionAssert.AreEqual(new[] { "A" }, result.ToList());
    }

    // ── Serialize / Deserialize ──────────────────────────────────────────────

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

        // Pollute with extra nodes
        store.AddNode(new GraphNode("EXTRA", "Record"));

        store.Deserialize(json);

        Assert.AreEqual(4, store.NodeCount, "Deserialize must replace the graph, not merge");
        Assert.IsFalse(store.TryGetNode("EXTRA", out _));
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Clear_ResetsNodeAndEdgeCount()
    {
        var store = BuildSampleGraph();
        store.Clear();

        Assert.AreEqual(0, store.NodeCount);
        Assert.AreEqual(0, store.EdgeCount);
    }

    [TestMethod]
    public void Clear_TryGetNode_ReturnsFalse()
    {
        var store = BuildSampleGraph();
        store.Clear();

        Assert.IsFalse(store.TryGetNode("A", out _));
    }

    // ── ImpactBackward ───────────────────────────────────────────────────────

    /// <summary>
    /// Member roll-up: CallerType.method --CALLS--> StartType.method.
    /// BfsBackward returns [] (no type→type CALLS), ImpactBackward returns CallerType.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_MemberCallsMember_RollsUpToCallerType()
    {
        // StartType --CONTAINS--> StartType.method
        // CallerType --CONTAINS--> CallerType.method
        // CallerType.method --CALLS--> StartType.method
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var startMethod = new GraphNode("StartType.DoWork", "Method");
        var callerType = new GraphNode("CallerType", "Class");
        var callerMethod = new GraphNode("CallerType.Execute", "Method");

        store.AddEdge(new GraphEdge(startType, startMethod, "CONTAINS"));
        store.AddEdge(new GraphEdge(callerType, callerMethod, "CONTAINS"));
        store.AddEdge(new GraphEdge(callerMethod, startMethod, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };

        var bfsResult = store.BfsBackward("StartType", edgeTypes);
        Assert.AreEqual(0, bfsResult.Count, "BfsBackward must see no inbound edges on the type node");

        var impactResult = store.ImpactBackward("StartType", edgeTypes);
        CollectionAssert.AreEqual(new[] { "CallerType" }, impactResult.ToList());
    }

    /// <summary>
    /// Static utility class scenario: Guard.Check is called by multiple types' methods.
    /// ImpactBackward on Guard returns all calling types.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_StaticUtility_ReturnsAllCallerTypes()
    {
        // Guard --CONTAINS--> Guard.Check
        // ServiceA --CONTAINS--> ServiceA.Validate
        // ServiceB --CONTAINS--> ServiceB.Process
        // ServiceA.Validate --CALLS--> Guard.Check
        // ServiceB.Process --CALLS--> Guard.Check
        var store = new GraphStore();
        var guard = new GraphNode("Guard", "Class");
        var guardCheck = new GraphNode("Guard.Check", "Method");
        var serviceA = new GraphNode("ServiceA", "Class");
        var serviceAValidate = new GraphNode("ServiceA.Validate", "Method");
        var serviceB = new GraphNode("ServiceB", "Class");
        var serviceBProcess = new GraphNode("ServiceB.Process", "Method");

        store.AddEdge(new GraphEdge(guard, guardCheck, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceA, serviceAValidate, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceB, serviceBProcess, "CONTAINS"));
        store.AddEdge(new GraphEdge(serviceAValidate, guardCheck, "CALLS"));
        store.AddEdge(new GraphEdge(serviceBProcess, guardCheck, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Guard", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ServiceA", "ServiceB" }, result);
    }

    /// <summary>
    /// Interface bridging: IRepository is injected into Controller.
    /// ImpactBackward on ConcreteRepository (implements IRepository) returns Controller.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_ConcreteClass_ReachesInterfaceConsumers()
    {
        // ConcreteRepository --IMPLEMENTS--> IRepository
        // Controller --INJECTS--> IRepository
        var store = new GraphStore();
        var concreteRepo = new GraphNode("ConcreteRepository", "Class");
        var iRepo = new GraphNode("IRepository", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store.AddEdge(new GraphEdge(concreteRepo, iRepo, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(controller, iRepo, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("ConcreteRepository", edgeTypes);

        CollectionAssert.AreEqual(new[] { "Controller" }, result.ToList());
    }

    /// <summary>
    /// Multi-interface bridging: a class implementing two interfaces reaches consumers of both.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_MultipleInterfaces_BridgesThroughAll()
    {
        // Service --IMPLEMENTS--> IFoo
        // Service --IMPLEMENTS--> IBar
        // ConsumerA --INJECTS--> IFoo
        // ConsumerB --INJECTS--> IBar
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iFoo = new GraphNode("IFoo", "Interface");
        var iBar = new GraphNode("IBar", "Interface");
        var consumerA = new GraphNode("ConsumerA", "Class");
        var consumerB = new GraphNode("ConsumerB", "Class");

        store.AddEdge(new GraphEdge(service, iFoo, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(service, iBar, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(consumerA, iFoo, "INJECTS"));
        store.AddEdge(new GraphEdge(consumerB, iBar, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Service", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ConsumerA", "ConsumerB" }, result);
    }

    /// <summary>
    /// Transitive bridging: Controller injects IOuter; Outer implements IOuter.
    /// ImpactBackward on Outer bridges through IOuter and returns Controller,
    /// exercising the interface-bridging seed without a depth parameter.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_TransitiveChain_ControllerInjectsInterfaceImplementedByConcrete()
    {
        // Outer --IMPLEMENTS--> IOuter
        // Controller --INJECTS--> IOuter
        // ServiceB --INJECTS--> Outer   (direct, to verify non-interface path still works)
        var store = new GraphStore();
        var outer = new GraphNode("Outer", "Class");
        var iOuter = new GraphNode("IOuter", "Interface");
        var controller = new GraphNode("Controller", "Class");
        var serviceB = new GraphNode("ServiceB", "Class");

        store.AddEdge(new GraphEdge(outer, iOuter, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(controller, iOuter, "INJECTS"));
        store.AddEdge(new GraphEdge(serviceB, outer, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Outer", edgeTypes).OrderBy(x => x).ToList();

        // Controller reaches Outer via interface bridge; ServiceB directly injects Outer.
        CollectionAssert.AreEqual(new[] { "Controller", "ServiceB" }, result);
        CollectionAssert.DoesNotContain(result, "IOuter");
        CollectionAssert.DoesNotContain(result, "Outer");
    }

    /// <summary>
    /// REFERENCES and RETURNS inbound edges are rolled up to the type level.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_ReferencesAndReturnsEdges_RolledUpToType()
    {
        // StartType
        // UserType --CONTAINS--> UserType.Prop (REFERENCES StartType)
        // ProviderType --CONTAINS--> ProviderType.Get (RETURNS StartType)
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var userType = new GraphNode("UserType", "Class");
        var userProp = new GraphNode("UserType.Prop", "Property");
        var providerType = new GraphNode("ProviderType", "Class");
        var providerGet = new GraphNode("ProviderType.Get", "Method");

        store.AddNode(startType);
        store.AddEdge(new GraphEdge(userType, userProp, "CONTAINS"));
        store.AddEdge(new GraphEdge(userProp, startType, "REFERENCES"));
        store.AddEdge(new GraphEdge(providerType, providerGet, "CONTAINS"));
        store.AddEdge(new GraphEdge(providerGet, startType, "RETURNS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("StartType", edgeTypes).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { "ProviderType", "UserType" }, result);
    }

    /// <summary>
    /// Seed nodes (start type, its members, bridged interfaces, their members) must not appear
    /// in the result.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_SeedNodes_ExcludedFromResult()
    {
        // StartType --CONTAINS--> StartType.M
        // StartType --IMPLEMENTS--> IFace
        // IFace --CONTAINS--> IFace.Contract
        // CallerType --CONTAINS--> CallerType.Use
        // CallerType.Use --CALLS--> StartType.M
        var store = new GraphStore();
        var startType = new GraphNode("StartType", "Class");
        var startM = new GraphNode("StartType.M", "Method");
        var iFace = new GraphNode("IFace", "Interface");
        var ifaceContract = new GraphNode("IFace.Contract", "Method");
        var callerType = new GraphNode("CallerType", "Class");
        var callerUse = new GraphNode("CallerType.Use", "Method");

        store.AddEdge(new GraphEdge(startType, startM, "CONTAINS"));
        store.AddEdge(new GraphEdge(startType, iFace, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(iFace, ifaceContract, "CONTAINS"));
        store.AddEdge(new GraphEdge(callerType, callerUse, "CONTAINS"));
        store.AddEdge(new GraphEdge(callerUse, startM, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("StartType", edgeTypes);

        CollectionAssert.DoesNotContain(result.ToList(), "StartType");
        CollectionAssert.DoesNotContain(result.ToList(), "StartType.M");
        CollectionAssert.DoesNotContain(result.ToList(), "IFace");
        CollectionAssert.DoesNotContain(result.ToList(), "IFace.Contract");
        CollectionAssert.AreEqual(new[] { "CallerType" }, result.ToList());
    }

    /// <summary>
    /// Self-referential graph: a method that calls itself — must terminate.
    /// </summary>
    [TestMethod]
    [Timeout(5_000)]
    public void ImpactBackward_SelfReferential_Terminates()
    {
        // RecursiveType --CONTAINS--> RecursiveType.Recurse
        // RecursiveType.Recurse --CALLS--> RecursiveType.Recurse (self-loop)
        var store = new GraphStore();
        var t = new GraphNode("RecursiveType", "Class");
        var m = new GraphNode("RecursiveType.Recurse", "Method");

        store.AddEdge(new GraphEdge(t, m, "CONTAINS"));
        store.AddEdge(new GraphEdge(m, m, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("RecursiveType", edgeTypes);

        // The only caller of Recurse is itself (a seed member), so nothing reported.
        Assert.AreEqual(0, result.Count);
    }

    /// <summary>
    /// Cyclic type graph: A.m calls B.m, B.m calls A.m — must terminate and report each type once.
    /// </summary>
    [TestMethod]
    [Timeout(5_000)]
    public void ImpactBackward_CyclicTypes_TerminatesAndDeduplicates()
    {
        // TypeA --CONTAINS--> TypeA.M
        // TypeB --CONTAINS--> TypeB.M
        // TypeA.M --CALLS--> TypeB.M
        // TypeB.M --CALLS--> TypeA.M
        var store = new GraphStore();
        var typeA = new GraphNode("TypeA", "Class");
        var typeAM = new GraphNode("TypeA.M", "Method");
        var typeB = new GraphNode("TypeB", "Class");
        var typeBM = new GraphNode("TypeB.M", "Method");

        store.AddEdge(new GraphEdge(typeA, typeAM, "CONTAINS"));
        store.AddEdge(new GraphEdge(typeB, typeBM, "CONTAINS"));
        store.AddEdge(new GraphEdge(typeAM, typeBM, "CALLS"));
        store.AddEdge(new GraphEdge(typeBM, typeAM, "CALLS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("TypeA", edgeTypes);

        // TypeB calls TypeA.M, so TypeB is impacted.
        // TypeA.M is a seed, TypeA is start — neither reported.
        // TypeB appears exactly once.
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("TypeB", result[0]);
    }

    /// <summary>
    /// UnknownNode start returns empty without throwing.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_UnknownStartNode_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var result = store.ImpactBackward("NonExistent", new HashSet<string> { "CALLS" });

        Assert.AreEqual(0, result.Count);
    }

    /// <summary>
    /// Two-hop cross-layer chain seeded at the concrete Service:
    ///   Controller --INJECTS--> IOrchestrator
    ///   Orchestrator --IMPLEMENTS--> IOrchestrator
    ///   Orchestrator --INJECTS--> IService
    ///   Service --IMPLEMENTS--> IService
    ///
    /// ImpactBackward("Service", ...) must bridge Service→IService, then discover Orchestrator
    /// (which injects IService), then bridge Orchestrator→IOrchestrator, then discover Controller
    /// (which injects IOrchestrator). Both Orchestrator and Controller must appear; interfaces
    /// and Service itself must be suppressed.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_TwoHopCrossLayer_ServiceSeed_ReachesController()
    {
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iService = new GraphNode("IService", "Interface");
        var orchestrator = new GraphNode("Orchestrator", "Class");
        var iOrchestrator = new GraphNode("IOrchestrator", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store.AddEdge(new GraphEdge(service, iService, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(orchestrator, iOrchestrator, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(orchestrator, iService, "INJECTS"));
        store.AddEdge(new GraphEdge(controller, iOrchestrator, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("Service", edgeTypes);

        CollectionAssert.Contains(result.ToList(), "Orchestrator");
        CollectionAssert.Contains(result.ToList(), "Controller");
        CollectionAssert.DoesNotContain(result.ToList(), "Service");
        CollectionAssert.DoesNotContain(result.ToList(), "IService");
        CollectionAssert.DoesNotContain(result.ToList(), "IOrchestrator");
    }

    /// <summary>
    /// Two-hop cross-layer chain seeded at the interface IService:
    ///   Controller --INJECTS--> IOrchestrator
    ///   Orchestrator --IMPLEMENTS--> IOrchestrator
    ///   Orchestrator --INJECTS--> IService
    ///   Service --IMPLEMENTS--> IService
    ///
    /// ImpactBackward("IService", ...) must discover Orchestrator (which injects IService),
    /// then bridge Orchestrator→IOrchestrator, then discover Controller (which injects IOrchestrator).
    /// Both Orchestrator and Controller must appear; interfaces must be suppressed.
    /// </summary>
    [TestMethod]
    public void ImpactBackward_TwoHopCrossLayer_IServiceSeed_ReachesController()
    {
        var store = new GraphStore();
        var service = new GraphNode("Service", "Class");
        var iService = new GraphNode("IService", "Interface");
        var orchestrator = new GraphNode("Orchestrator", "Class");
        var iOrchestrator = new GraphNode("IOrchestrator", "Interface");
        var controller = new GraphNode("Controller", "Class");

        store.AddEdge(new GraphEdge(service, iService, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(orchestrator, iOrchestrator, "IMPLEMENTS"));
        store.AddEdge(new GraphEdge(orchestrator, iService, "INJECTS"));
        store.AddEdge(new GraphEdge(controller, iOrchestrator, "INJECTS"));

        var edgeTypes = new HashSet<string> { "CALLS", "INJECTS", "REFERENCES", "RETURNS" };
        var result = store.ImpactBackward("IService", edgeTypes);

        CollectionAssert.Contains(result.ToList(), "Orchestrator");
        CollectionAssert.Contains(result.ToList(), "Controller");
        CollectionAssert.DoesNotContain(result.ToList(), "IService");
        CollectionAssert.DoesNotContain(result.ToList(), "IOrchestrator");
    }

    // ── Concurrency: snapshot-swap (§1.1 / AC1) ───────────────────────────────
    // Readers must never throw InvalidOperationException (collection modified) and never observe
    // a half-built graph while a rebuild is staged; the committed snapshot must be visible after.

    [TestMethod]
    [Timeout(15_000)]
    public void ConcurrentReaders_AmidRebuild_NeverThrowAndObserveCommittedSnapshot()
    {
        var store = BuildSampleGraph();

        Exception? readerException = null;
        var writerDone = new ManualResetEventSlim(false);

        // Multiple readers hammer reads for the whole rebuild window.
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

        // One writer: stage a rebuild, build a much larger graph into staging, then commit.
        // While staging, the published _state is untouched, so readers keep seeing A..D.
        var writer = Task.Run(() =>
        {
            store.BeginRebuild();
            store.AddNode(new GraphNode("A", "Class"));
            store.AddNode(new GraphNode("B", "Class"));
            store.AddNode(new GraphNode("C", "Class"));
            store.AddEdge(new GraphEdge(new GraphNode("A", "Class"), new GraphNode("B", "Class"), "CALLS"));
            store.AddEdge(new GraphEdge(new GraphNode("B", "Class"), new GraphNode("C", "Class"), "CALLS"));

            for (var i = 0; i < 300; i++)
            {
                store.AddNode(new GraphNode($"N{i}", "Class"));
                store.AddEdge(new GraphEdge(
                    new GraphNode("C", "Class"),
                    new GraphNode($"N{i}", "Class"),
                    "CALLS"));
            }

            store.CommitRebuild();
            writerDone.Set();
        });

        Task.WaitAll(readers);
        writer.Wait();

        Assert.IsNull(readerException,
            $"Readers must not throw during a concurrent rebuild. Got: {readerException}");

        // The committed snapshot must be visible to a post-commit read.
        Assert.IsTrue(store.NodeCount >= 300, "Committed rebuild must be visible after CommitRebuild.");
        var neighbors = store.GetAggregatedNeighbors("A");
        Assert.IsTrue(neighbors.Any(n => n.Id == "B"),
            "Post-commit read must see the rebuilt graph (A → B).");
    }
}
