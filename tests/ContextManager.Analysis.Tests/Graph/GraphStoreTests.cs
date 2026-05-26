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

    // ── GetNeighbors ─────────────────────────────────────────────────────────

    [TestMethod]
    public void GetNeighbors_NodeWithOutEdges_ReturnsTargets()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetNeighbors("A").Select(n => n.Id).Order().ToList();

        // A -> B (CALLS), A -> D (INJECTS)
        CollectionAssert.AreEqual(new[] { "B", "D" }, neighbors);
    }

    [TestMethod]
    public void GetNeighbors_NodeWithInEdgesOnly_ReturnsSources()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetNeighbors("C").Select(n => n.Id).Order().ToList();

        // B -> C (only in-edge)
        CollectionAssert.AreEqual(new[] { "B" }, neighbors);
    }

    [TestMethod]
    public void GetNeighbors_NodeWithBothInAndOutEdges_ReturnsAll()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetNeighbors("B").Select(n => n.Id).Order().ToList();

        // Out: C (CALLS), D (INJECTS) — In: A (CALLS)
        CollectionAssert.AreEqual(new[] { "A", "C", "D" }, neighbors);
    }

    [TestMethod]
    public void GetNeighbors_UnknownNode_ReturnsEmpty()
    {
        var store = BuildSampleGraph();
        var neighbors = store.GetNeighbors("Z");

        Assert.AreEqual(0, neighbors.Count);
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
}
