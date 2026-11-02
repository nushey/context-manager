using System.Text;
using System.Text.Json;
using ContextManager.Analysis.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphSynchronizerTests : FixtureTest
{
    private static readonly IReadOnlySet<string> ImpactEdgeTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CALLS", "INJECTS", "REFERENCES", "RETURNS", "INHERITS" };

    private string CopyFixture() => CopyFixture("GraphFixtures", "IncrementalFixtures");

    private const string IncrementalSolution = "Incremental.sln";

    private static string Sln(string directory, string solution = IncrementalSolution) => Path.Combine(directory, solution);

    private static string Src(string directory, string relative) => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void Edit(string directory, string relative, string from, string to) =>
        FixtureCopy.Edit(Src(directory, relative), from, to);

    private sealed record SyncOutcome(GraphStore Graph, bool Changed, IReadOnlyList<string> ExtractedDocuments);

    private static string[] Relative(SyncOutcome result, string directory) =>
        result.ExtractedDocuments.Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).Order().ToArray();

    private async Task<SyncOutcome> SyncAsync(
        GraphSynchronizer synchronizer,
        string directory,
        bool reevaluate = false,
        string solution = IncrementalSolution)
    {
        var before = RegistryOf(synchronizer).TryLoad(Sln(directory, solution))?.Capture() ?? new GraphStore();
        var result = await synchronizer.SynchronizeAsync(Sln(directory, solution), reevaluate);
        Assert.IsNotNull(result);
        StringAssert.Matches(result.Report.Status, new System.Text.RegularExpressions.Regex("^(complete|partial)$"));

        var extracted = result.Graph.Documents.Values
            .Where(d => !before.Documents.TryGetValue(d.Key, out var previous) || !ReferenceEquals(previous.Nodes, d.Nodes))
            .Select(d => d.Path)
            .ToList();
        return new SyncOutcome(result.Graph, result.Graph.Revision != before.Revision, extracted);
    }

    private async Task AssertMatchesFreshScanAsync(GraphStore incremental, string directory, string solution = IncrementalSolution)
    {
        var copy = CopyDirectory(directory);

        var fresh = await SyncAsync(CreateSynchronizer(new GraphRegistry()), copy, solution: solution);

        Assert.AreEqual(Shape(fresh.Graph, copy), Shape(incremental, directory));
    }

    private static string Shape(GraphStore store, string root)
    {
        var builder = new StringBuilder($"C {store.NodeCount} {store.EdgeCount}\n");
        using var json = JsonDocument.Parse(store.Serialize());
        builder.AppendLine(json.RootElement.GetProperty("documents").GetRawText());
        var ids = store.Documents.Values
            .OrderBy(d => d.ProjectOrder)
            .ThenBy(d => d.DocumentOrder)
            .SelectMany(d => d.Nodes.Select(n => n.Id).Concat(d.Edges.SelectMany(e => new[] { e.Source.Id, e.Target.Id })))
            .Distinct()
            .ToList();

        foreach (var id in ids)
        {
            builder.AppendLine($"D {id} {JsonSerializer.Serialize(store.GetAggregatedNeighbors(id), AnalysisJson.Options)}");
            builder.AppendLine($"I {id} {string.Join(",", store.ImpactBackward(id, ImpactEdgeTypes))}");
            foreach (var target in ids)
                builder.AppendLine($"S {id} {target} {string.Join(",", store.ShortestPath(id, target))}");
        }

        return builder.ToString().Replace(JsonEncodedText.Encode(root).ToString(), "<root>").Replace(root, "<root>");
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task PreviousMetadata_ReextractsOnceAndFailurePreservesPreviousGraph()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory);
        var path = GraphRegistry.GetGraphPath(Sln(directory));
        var payload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        RemoveMetadata(payload);
        var previousJson = payload.ToJsonString();
        File.WriteAllText(path, previousJson);
        var registry = new GraphRegistry();
        var previous = registry.TryLoad(Sln(directory))!.Capture();
        Assert.IsFalse(previous.HasSearchMetadata);
        Assert.AreEqual(initial.Graph.NodeCount, previous.NodeCount);
        using (var failing = new GraphSynchronizer(new GraphBuilder(new EdgeExtractor()), registry,
            () => throw new InvalidOperationException("Unavailable MSBuild")))
        {
            await Assert.ThrowsExactlyAsync<GraphSyncException>(() => failing.SynchronizeAsync(Sln(directory), false));
            Assert.AreEqual(previousJson, File.ReadAllText(path));
            Assert.AreEqual(previous.Serialize(), registry.TryLoad(Sln(directory))!.Serialize());
        }
        var migratedSynchronizer = CreateSynchronizer(registry);
        var migrated = await SyncAsync(migratedSynchronizer, directory);
        Assert.IsTrue(migrated.Graph.HasSearchMetadata);
        Assert.AreEqual(initial.Graph.Documents.Count, migrated.ExtractedDocuments.Count);
        Assert.IsTrue(migrated.Changed);
        var persisted = (File.ReadAllText(path), File.GetLastWriteTimeUtc(path));
        var unchanged = await SyncAsync(migratedSynchronizer, directory);
        Assert.AreEqual(0, unchanged.ExtractedDocuments.Count);
        Assert.IsFalse(unchanged.Changed);
        Assert.AreEqual(persisted, (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)));

        static void RemoveMetadata(System.Text.Json.Nodes.JsonNode node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                obj.Remove("name");
                obj.Remove("namespace");
                obj.Remove("namespaces");
                foreach (var child in obj.ToList())
                    if (child.Value is not null)
                        RemoveMetadata(child.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
                foreach (var child in array)
                    if (child is not null)
                        RemoveMetadata(child);
        }
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task MissingGraph_BuildsPersistsProvenanceAndMatchesFreshScan()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Changed);
        CollectionAssert.AreEqual(
            new[] { "Consumer/Client.cs", "Core/Audit.cs", "Core/Envelope.Part1.cs", "Core/Envelope.Part2.cs", "Core/Sender.cs", "Independent/Standalone.cs" },
            Relative(result, directory));
        var persisted = new GraphStore();
        persisted.Deserialize(File.ReadAllText(GraphRegistry.GetGraphPath(Sln(directory))));
        Assert.AreEqual(result.Graph.Revision, persisted.Revision);
        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.Sender", out var sender));
        Assert.AreEqual(Src(directory, "Core/Sender.cs"), sender!.File);
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task NoChange_ExtractsNothingAndDoesNotRewriteGraph()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        var graphPath = GraphRegistry.GetGraphPath(Sln(directory));
        var before = (File.GetLastWriteTimeUtc(graphPath), File.ReadAllText(graphPath));

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsFalse(result.Changed);
        Assert.AreEqual(0, result.ExtractedDocuments.Count);
        Assert.AreEqual(before, (File.GetLastWriteTimeUtc(graphPath), File.ReadAllText(graphPath)));
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task BodyOnlyChange_WarmSession_ReextractsOnlyThatDocument()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(new[] { "Core/Sender.cs" }, Relative(result, directory));
        Assert.IsFalse(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Sender").Any(n => n.Id == "IncrementalFixtures.Core.Audit"));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task PartialTypeBodyChange_KeepsTheOtherDeclarationContribution()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Envelope.Part2.cs", "        Channel.Send(\"payload\");\n", "        Audit.Record();\n");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(new[] { "Core/Envelope.Part2.cs" }, Relative(result, directory));
        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.Envelope.Channel", out _));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task OverloadAddition_ReextractsDeclaringAndConsumerProjectsButNotIndependent()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "    public void Send(object message)", "    public void Send(string message) { }\n\n    public void Send(object message)");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(
            new[] { "Consumer/Client.cs", "Core/Audit.cs", "Core/Envelope.Part1.cs", "Core/Envelope.Part2.cs", "Core/Sender.cs" },
            Relative(result, directory));
        Assert.AreEqual(1, result.Graph.ShortestPath("IncrementalFixtures.Consumer.Client.Run()", "IncrementalFixtures.Core.Sender.Send(string)").Count - 1,
            "The unchanged consumer must rebind to the new overload.");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ConstantInitializerChange_PropagatesToConsumerProject()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Audit.cs", "    public static void Record() { }", "    public const int Size = 1;\n\n    public static void Record() { }");
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Audit.cs", "Size = 1;", "Size = 2;");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.Contains(Relative(result, directory), "Consumer/Client.cs");
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ContentChangeWithBackdatedTimestamp_IsDetected()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        var path = Src(directory, "Independent/Standalone.cs");
        var timestamp = File.GetLastWriteTimeUtc(path);
        Edit(directory, "Independent/Standalone.cs", "        Helper();\n", "");
        File.SetLastWriteTimeUtc(path, timestamp.AddDays(-1));

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(new[] { "Independent/Standalone.cs" }, Relative(result, directory));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task DeletedDeclaringFile_ReevaluatesAndKeepsIndependentContributions()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");
        File.Delete(Src(directory, "Core/Audit.cs"));

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(
            new[] { "Consumer/Client.cs", "Core/Envelope.Part1.cs", "Core/Envelope.Part2.cs", "Core/Sender.cs" },
            Relative(result, directory));
        Assert.IsFalse(result.Graph.TryGetNode("IncrementalFixtures.Core.Audit", out _));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task FormerlyUnresolvedReference_BecomesResolvableWhenDeclarationIsAdded()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        Edit(directory, "Consumer/Client.cs", "    public void Run()", "    public Extra? Pending { get; set; }\n\n    public void Run()");
        await SyncAsync(synchronizer, directory);
        File.WriteAllText(Src(directory, "Core/Extra.cs"), "namespace IncrementalFixtures.Core;\n\npublic class Extra { }\n");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.Contains(Relative(result, directory), "Core/Extra.cs");
        CollectionAssert.Contains(Relative(result, directory), "Consumer/Client.cs");
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        Assert.IsTrue(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Consumer.Client")
            .Any(n => n.Id == "IncrementalFixtures.Core.Extra" && n.Direction == "out"));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task DocumentAddedBeforeExistingDocuments_KeepsShiftedContributions()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        File.WriteAllText(Src(directory, "Core/Aaa.cs"), "namespace IncrementalFixtures.Core;\n\npublic class Aaa\n{\n    public void Go() { }\n}\n");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.Aaa", out _));
        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.Audit", out _));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
        await AssertMatchesFreshScanAsync(new GraphRegistry().TryLoad(Sln(directory))!, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ProjectAddedBeforeExistingProjects_KeepsReusedContributions()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Directory.CreateDirectory(Src(directory, "Early"));
        File.Copy(Src(directory, "Independent/Independent.csproj"), Src(directory, "Early/Early.csproj"));
        File.WriteAllText(Src(directory, "Early/First.cs"), "namespace IncrementalFixtures.Early;\n\npublic class First\n{\n    public void Go() { }\n}\n");
        Edit(directory, "Incremental.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Core\"",
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Early\", \"Early\\Early.csproj\", \"{A4444444-4444-4444-4444-444444444444}\"\nEndProject\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Core\"");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Early.First", out _));
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
        await AssertMatchesFreshScanAsync(new GraphRegistry().TryLoad(Sln(directory))!, directory);
    }

    private const string CoreProjectEnd = "</Project>";

    private static void AddToCoreProject(string directory, string content) =>
        Edit(directory, "Core/Core.csproj", CoreProjectEnd, content + "\n" + CoreProjectEnd);

    [TestMethod]
    [Timeout(180_000)]
    public async Task ImportedTargetsOutsideProjectTree_ChangeIsDetectedWithoutForcedReevaluation()
    {
        var directory = CopyFixture();
        Directory.CreateDirectory(Src(directory, "build"));
        File.WriteAllText(Src(directory, "build/Common.targets"), "<Project>\n</Project>\n");
        AddToCoreProject(directory, "  <Import Project=\"..\\build\\Common.targets\" />");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");
        await SyncAsync(synchronizer, directory);
        Edit(directory, "build/Common.targets", "</Project>", "  <ItemGroup>\n    <Compile Remove=\"Audit.cs\" />\n  </ItemGroup>\n</Project>");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Changed);
        Assert.IsFalse(result.Graph.TryGetNode("IncrementalFixtures.Core.Audit", out _));
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task DirectoryBuildPropsChange_ReevaluatesMembership()
    {
        var directory = CopyFixture();
        File.WriteAllText(Src(directory, "Directory.Build.props"), "<Project>\n</Project>\n");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Directory.Build.props", "</Project>", "  <PropertyGroup>\n    <DefaultItemExcludes>$(DefaultItemExcludes);Standalone.cs</DefaultItemExcludes>\n  </PropertyGroup>\n</Project>");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsFalse(result.Graph.TryGetNode("IncrementalFixtures.Independent.Standalone", out _));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task OutOfTreeCompileGlob_NewMatchingFileIsDetected()
    {
        var directory = CopyFixture();
        Directory.CreateDirectory(Src(directory, "shared"));
        File.WriteAllText(Src(directory, "shared/LinkedOne.cs"), "namespace IncrementalFixtures.Core;\n\npublic class LinkedOne { }\n");
        AddToCoreProject(directory, "  <ItemGroup>\n    <Compile Include=\"..\\shared\\*.cs\" />\n  </ItemGroup>");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        File.WriteAllText(Src(directory, "shared/LinkedTwo.cs"), "namespace IncrementalFixtures.Core;\n\npublic class LinkedTwo { }\n");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.LinkedTwo", out _));
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task LinkedFileBodyChange_WarmSession_ReextractsOnlyThatDocument()
    {
        var directory = CopyFixture();
        Directory.CreateDirectory(Src(directory, "shared"));
        File.WriteAllText(Src(directory, "shared/Linked.cs"),
            "namespace IncrementalFixtures.Core;\n\npublic class Linked\n{\n    public void Go()\n    {\n        Audit.Record();\n    }\n}\n");
        AddToCoreProject(directory, "  <ItemGroup>\n    <Compile Include=\"..\\shared\\Linked.cs\" Link=\"Linked.cs\" />\n  </ItemGroup>");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "shared/Linked.cs", "        Audit.Record();\n", "");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(new[] { "shared/Linked.cs" }, Relative(result, directory));
        Assert.IsFalse(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Linked").Any(n => n.Id == "IncrementalFixtures.Core.Audit"));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GlobalUsingChange_RebindsUnchangedDocuments()
    {
        var directory = CopyFixture();
        File.WriteAllText(Src(directory, "Core/Tools.cs"),
            "namespace IncrementalFixtures.Core.First\n{\n    public static class Tool { public static void Use() { } }\n}\n\n" +
            "namespace IncrementalFixtures.Core.Second\n{\n    public static class Tool { public static void Use() { } }\n}\n");
        File.WriteAllText(Src(directory, "Core/GlobalUsings.cs"), "global using IncrementalFixtures.Core.First;\n");
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "        Audit.Record();\n        Tool.Use();\n");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/GlobalUsings.cs", "Core.First", "Core.Second");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.Contains(Relative(result, directory), "Core/Sender.cs");
        Assert.IsTrue(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Sender")
            .Any(n => n.Id == "IncrementalFixtures.Core.Second.Tool" && n.Direction == "out"));
        Assert.IsFalse(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.Sender")
            .Any(n => n.Id == "IncrementalFixtures.Core.First.Tool"));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task NullableDirectiveChange_MatchesFreshScan()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "namespace IncrementalFixtures.Core;", "#nullable disable\nnamespace IncrementalFixtures.Core;");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.Contains(Relative(result, directory), "Core/Sender.cs");
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GeneratorOutputChange_ReextractsGeneratingProjectAndDependents()
    {
        var directory = CopyFixture();
        File.WriteAllText(Src(directory, "Core/names.txt"), "Alpha\n");
        AddToCoreProject(directory,
            $"  <ItemGroup>\n    <Analyzer Include=\"{typeof(NamesGenerator).Assembly.Location}\" />\n    <AdditionalFiles Include=\"names.txt\" />\n  </ItemGroup>");
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "        Audit.Record();\n        Names.Alpha();\n");
        Edit(directory, "Consumer/Client.cs", "        sender.Send(\"hello\");\n", "        sender.Send(\"hello\");\n        Names.Alpha();\n");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory);
        Assert.IsTrue(initial.Graph.TryGetNode("IncrementalFixtures.Core.Names.Alpha()", out _),
            "The generated member must be bound for the scenario to be meaningful.");
        await SyncAsync(synchronizer, directory);
        File.WriteAllText(Src(directory, "Core/names.txt"), "Beta\n");

        var result = await SyncAsync(synchronizer, directory);

        CollectionAssert.AreEqual(
            new[] { "Consumer/Client.cs", "Core/Audit.cs", "Core/Envelope.Part1.cs", "Core/Envelope.Part2.cs", "Core/Sender.cs" },
            Relative(result, directory));
        Assert.IsFalse(result.Graph.TryGetNode("IncrementalFixtures.Core.Names.Alpha()", out _));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task AnalyzerConfigChange_RebindsGeneratedCallsAndMatchesFreshScan()
    {
        var directory = CopyFixture();
        File.WriteAllText(Src(directory, "Core/.editorconfig"), "root = true\n[*.cs]\ncm_target_base = Alpha\n");
        File.WriteAllText(Src(directory, "Core/ConfigCaller.cs"),
            "namespace IncrementalFixtures.Core; public class Alpha { public void Go() { } } public class Beta { public void Go() { } } public class ConfigCaller { public void Use() { new ConfigTarget().Go(); } }");
        AddToCoreProject(directory, $"  <ItemGroup>\n    <Analyzer Include=\"{typeof(NamesGenerator).Assembly.Location}\" />\n  </ItemGroup>");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory);
        Assert.IsTrue(initial.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Alpha" && n.EdgeKinds.ContainsKey("CALLS")));
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/.editorconfig", "Alpha", "Beta");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(result.Changed);
        Assert.IsTrue(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Beta" && n.EdgeKinds.ContainsKey("CALLS")));
        Assert.IsFalse(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Alpha"));
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task AnalyzerConfigAddedAndRemovedUnderSourceDirectory_IsDetected()
    {
        var directory = CopyFixture();
        Directory.CreateDirectory(Src(directory, "Core/Nested"));
        File.WriteAllText(Src(directory, "Core/Nested/ConfigCaller.cs"),
            "namespace IncrementalFixtures.Core; public class Alpha { public void Go() { } } public class Beta { public void Go() { } } public class ConfigCaller { public void Use() { new ConfigTarget().Go(); } }");
        File.WriteAllText(Src(directory, "Core/.editorconfig"), "root = true\n[*.cs]\ncm_target_base = Alpha\n");
        AddToCoreProject(directory, $"  <ItemGroup>\n    <Analyzer Include=\"{typeof(NamesGenerator).Assembly.Location}\" />\n  </ItemGroup>");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory);
        Assert.IsTrue(initial.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Alpha"));
        File.WriteAllText(Src(directory, "Core/Nested/.editorconfig"), "[*.cs]\ncm_target_base = Beta\n");

        var added = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(added.Changed);
        Assert.IsTrue(added.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Beta"));
        await AssertMatchesFreshScanAsync(added.Graph, directory);
        File.Delete(Src(directory, "Core/Nested/.editorconfig"));

        var removed = await SyncAsync(synchronizer, directory);

        Assert.IsTrue(removed.Changed);
        Assert.IsTrue(removed.Graph.GetAggregatedNeighbors("IncrementalFixtures.Core.ConfigCaller")
            .Any(n => n.Id == "IncrementalFixtures.Core.Alpha"));
        CollectionAssert.DoesNotContain(Relative(removed, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(removed.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ColdSession_RemovingFirstProjectWithSharedNodeId_PreservesReusedDeclarationFile()
    {
        var directory = CopyFixture();
        File.WriteAllText(Src(directory, "Independent/Duplicate.cs"),
            "namespace IncrementalFixtures.Core; public static class Audit { public static void Record() { } }");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory);
        Assert.IsTrue(initial.Graph.TryGetNode("IncrementalFixtures.Core.Audit", out var first));
        Assert.AreEqual(Src(directory, "Core/Audit.cs"), first!.File);
        synchronizer.Dispose();
        var solution = File.ReadAllText(Sln(directory));
        solution = System.Text.RegularExpressions.Regex.Replace(solution,
            "Project\\([^\\r\\n]+ = \"(Core|Consumer)\"[^\\r\\n]+\\r?\\nEndProject\\r?\\n", string.Empty);
        File.WriteAllText(Sln(directory), solution);

        var result = await SyncAsync(CreateSynchronizer(new GraphRegistry()), directory);

        Assert.AreEqual(0, result.ExtractedDocuments.Count);
        Assert.IsTrue(result.Graph.TryGetNode("IncrementalFixtures.Core.Audit", out var survivor));
        Assert.AreEqual(Src(directory, "Independent/Duplicate.cs"), survivor!.File);
        await AssertMatchesFreshScanAsync(result.Graph, directory);
        await AssertMatchesFreshScanAsync(new GraphRegistry().TryLoad(Sln(directory))!, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task LegacyFrameworkProject_SyncsIncrementallyAlongsideSdkProjects()
    {
        const string coverage = "Coverage.sln";
        var directory = CopyFixture("ScanFixtures");
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        var initial = await SyncAsync(synchronizer, directory, solution: coverage);
        Assert.IsTrue(initial.Graph.TryGetNode("ScanFixtures.LegacyType", out _));

        var unchanged = await SyncAsync(synchronizer, directory, solution: coverage);
        Edit(directory, "LegacyProject/LegacyType.cs", "public class LegacyType { }", "public class LegacyType { public void Use() { } }");
        var declarationEdit = await SyncAsync(synchronizer, directory, solution: coverage);
        File.WriteAllText(Src(directory, "LegacyProject/NotCompiled.cs"), "namespace ScanFixtures\n{\n    public class NotCompiled { }\n}\n");
        var nonMemberFile = await SyncAsync(synchronizer, directory, solution: coverage);

        Assert.IsFalse(unchanged.Changed, JsonSerializer.Serialize(initial.Graph.Baseline!.Inputs
            .Where(input => !unchanged.Graph.Baseline!.Inputs.Contains(input)), AnalysisJson.Options));
        Assert.AreEqual(0, unchanged.ExtractedDocuments.Count);
        CollectionAssert.AreEqual(new[] { "LegacyProject/LegacyType.cs" }, Relative(declarationEdit, directory));
        Assert.IsTrue(declarationEdit.Graph.TryGetNode("ScanFixtures.LegacyType.Use()", out _));
        Assert.IsFalse(nonMemberFile.Graph.TryGetNode("ScanFixtures.NotCompiled", out _));
        Assert.AreEqual(0, nonMemberFile.ExtractedDocuments.Count);
        await AssertMatchesFreshScanAsync(nonMemberFile.Graph, directory, coverage);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task CompileExclusionInProjectFile_RemovesExcludedSourceFromGraph()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        File.WriteAllText(Src(directory, "Core/Excluded.cs"), "namespace IncrementalFixtures.Core;\n\npublic class Excluded { }\n");
        await SyncAsync(synchronizer, directory);
        Assert.IsTrue((await SyncAsync(synchronizer, directory)).Graph.TryGetNode("IncrementalFixtures.Core.Excluded", out _));
        Edit(directory, "Core/Core.csproj", "  </PropertyGroup>", "  </PropertyGroup>\n\n  <ItemGroup>\n    <Compile Remove=\"Excluded.cs\" />\n  </ItemGroup>");

        var result = await SyncAsync(synchronizer, directory);

        Assert.IsFalse(result.Graph.TryGetNode("IncrementalFixtures.Core.Excluded", out _));
        CollectionAssert.DoesNotContain(Relative(result, directory), "Independent/Standalone.cs");
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ColdSession_ReusesPersistedContributionsOfUnrelatedProjects()
    {
        var directory = CopyFixture();
        await SyncAsync(CreateSynchronizer(new GraphRegistry()), directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");

        var result = await SyncAsync(CreateSynchronizer(new GraphRegistry()), directory);

        CollectionAssert.AreEqual(
            new[] { "Consumer/Client.cs", "Core/Audit.cs", "Core/Envelope.Part1.cs", "Core/Envelope.Part2.cs", "Core/Sender.cs" },
            Relative(result, directory));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task GraphReplacedByAnotherSession_DropsStaleWarmBaseline()
    {
        var directory = CopyFixture();
        var first = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(first, directory);
        await SyncAsync(first, directory);

        Edit(directory, "Independent/Standalone.cs", "        Helper();\n", "");
        await SyncAsync(CreateSynchronizer(new GraphRegistry()), directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");

        var result = await SyncAsync(first, directory);

        Assert.IsTrue(result.Changed);
        Assert.IsFalse(result.Graph.GetAggregatedNeighbors("IncrementalFixtures.Independent.Standalone").Any(n => n.Direction == "out" && n.EdgeKinds.ContainsKey("CALLS")));
        await AssertMatchesFreshScanAsync(result.Graph, directory);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ConcurrentSynchronizations_AreSerializedAndConverge()
    {
        var directory = CopyFixture();
        var synchronizer = CreateSynchronizer(new GraphRegistry());
        await SyncAsync(synchronizer, directory);
        Edit(directory, "Core/Sender.cs", "        Audit.Record();\n", "");

        var results = await Task.WhenAll(SyncAsync(synchronizer, directory), SyncAsync(synchronizer, directory));

        Assert.AreEqual(results[0].Graph.Revision, results[1].Graph.Revision, "The second writer must observe the first writer's commit.");
        await AssertMatchesFreshScanAsync(results.Last().Graph, directory);
    }

    [TestMethod]
    public async Task MissingSolutionAndGraph_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"context-manager-missing-{Guid.NewGuid():N}", "Missing.sln");

        Assert.IsNull(await CreateSynchronizer(new GraphRegistry()).SynchronizeAsync(missing, reevaluate: false));
    }
}
