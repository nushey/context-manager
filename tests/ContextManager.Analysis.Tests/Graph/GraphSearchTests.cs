using System.Text.Json;
using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphSearchTests
{
    internal static readonly GraphBaseline Baseline = new("/test/Test.sln", [], [], [], false);

    internal static async Task<GraphStore> FixtureGraphAsync()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Search", LanguageNames.CSharp);
        foreach (var name in new[] { "SearchCases.cs", "SearchGlobal.cs" })
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "GraphFixtures", name);
            project = workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(path)), VersionStamp.Default)),
                filePath: Path.Combine("/test/DirectoryMarker", name))).Project;
        }

        var extraction = await new GraphBuilder(new EdgeExtractor()).ExtractProjectAsync(project, "Search", 0, null);
        return new GraphStore().Stage([], extraction.Documents.Values.OfType<GraphDocumentRecord>().ToList(), Baseline);
    }

    internal static GraphStore Methods(int count, string name = "Login")
    {
        var nodes = Enumerable.Range(0, count).Select(i => new GraphNode($"N.C.{name}({i})", "Method", "/test/Other.cs", name, "N")).ToList();
        return new GraphStore().Stage([], [new GraphDocumentRecord("p", "/test/Other.cs", "", 0, 0, nodes, [], ["N"])], Baseline);
    }

    [TestMethod]
    public async Task Fixture_SearchesNamesOnlyAndPreservesOverloadsPrivateGenericsAndNamespaces()
    {
        var graph = await FixtureGraphAsync();
        var result = graph.Search(["lOgIn", "Login", "ConvertValue"]);
        Assert.AreEqual(5, result.Results.Sum(f => f.Methods.Count));
        var file = result.Results[0];
        Assert.IsFalse(file.FileMatched);
        CollectionAssert.AreEqual(new[] { "SearchFixtures.Authentication", "SearchFixtures.Second", "SearchFixtures.MetadataOnly" }, file.Namespaces.ToArray());
        Assert.AreEqual(2, file.Methods.Count(m => m.Name == "Login"));
        Assert.AreEqual(2, file.Methods.Where(m => m.Name == "Login").Select(m => m.NodeId).Distinct().Count());
        Assert.IsTrue(file.Methods.Any(m => m.Name == "ConvertValue" && m.NodeId.Contains("<T>")));
        Assert.AreEqual(0, result.Results.Single(f => f.File.EndsWith("SearchGlobal.cs")).Namespaces.Count);
        foreach (var term in new[] { "MetadataOnly", "DirectoryMarker", "ctor" })
            Assert.AreEqual(0, graph.Search([term]).Results.Count, term);
        var constructors = graph.Search(["Session"]).Results.SelectMany(f => f.Methods).ToList();
        CollectionAssert.AreEqual(new[] { "Session", "GlobalSession" }, constructors.Select(m => m.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "SearchFixtures.Authentication.Session.Session()", "GlobalSession.GlobalSession()" }, constructors.Select(m => m.NodeId).ToArray());
        Assert.IsTrue(graph.Search(["SignIn"]).Results.SelectMany(f => f.Methods).Any(m => m.NodeId.Contains("ISignIn.SignIn")));
    }

    [TestMethod]
    public async Task Fixture_FileOnlyMethodOnlyBothAndEmptySearch()
    {
        var graph = await FixtureGraphAsync();
        var fileOnly = graph.Search(["SearchCases"]).Results.Single();
        Assert.IsTrue(fileOnly.FileMatched);
        Assert.AreEqual(0, fileOnly.Methods.Count);
        var both = graph.Search(["SearchCases", "Login"]).Results.First();
        Assert.IsTrue(both.FileMatched);
        Assert.AreEqual(3, both.Methods.Count);
        Assert.AreEqual(0, graph.Search(["Missing"]).Results.Count);
        Assert.IsFalse(graph.Search(["Missing"]).Truncated);
    }

    [TestMethod]
    public void Limit_CountsUniqueMatchesAndDetectsOnlyAnAdditionalMatch()
    {
        foreach (var count in new[] { 49, 50, 51 })
        {
            var result = Methods(count).Search(["Login", "Login", "Log"]);
            Assert.AreEqual(Math.Min(count, 50), result.Results.Sum(f => f.Methods.Count));
            Assert.AreEqual(count > 50, result.Truncated);
        }
        var both = Methods(50).Search(["Other", "Login"]);
        Assert.IsTrue(both.Truncated);
        Assert.IsTrue(both.Results.Single().FileMatched);
        Assert.AreEqual(49, both.Results.Single().Methods.Count);
    }

    [TestMethod]
    public void ExactPass_PrecedesEarlierPartialsAndDoesNotBoostMultipleTerms()
    {
        var graph = Methods(51, "FindLogin");
        var exact = new GraphNode("N.C.Login()", "Method", "/test/Late.cs", "Login", "N");
        graph = graph.Stage([], [new GraphDocumentRecord("p", "/test/Late.cs", "", 0, 1, [exact], [], ["N"])], Baseline);
        var result = graph.Search(["Login", "Find"]);
        Assert.AreEqual("Login", result.Results[0].Methods[0].Name);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual("N.C.FindLogin(0)", result.Results[1].Methods[0].NodeId);
        var first = Methods(50).Search(["Login", "Other"]);
        Assert.IsTrue(first.Results.Single().FileMatched);
        var omittedFile = Methods(50).Search(["Login", "Oth"]);
        Assert.IsFalse(omittedFile.Results.Single().FileMatched);
        Assert.IsTrue(omittedFile.Truncated);
    }

    [TestMethod]
    public void EncounterOrder_PreservesDocumentOrderAndDoesNotBoostAdditionalTerms()
    {
        var first = new GraphDocumentRecord("p", "/test/First.cs", "", 0, 0,
            [new GraphNode("N.C.FooLogin()", "Method", "/test/First.cs", "FooLogin", "N")], [], ["N"]);
        var second = new GraphDocumentRecord("p", "/test/Second.cs", "", 0, 1,
            [new GraphNode("N.C.FindLogin()", "Method", "/test/Second.cs", "FindLogin", "N")], [], ["N"]);
        var graph = new GraphStore().Stage([], [second, first], Baseline);
        var result = graph.Search(["Login", "Find"]);
        Assert.AreEqual("/test/First.cs", result.Results[0].File);
        Assert.AreEqual("/test/Second.cs", result.Results[1].File);
        graph = graph.Stage([], [first with { Checksum = "changed" }], Baseline);
        Assert.AreEqual("/test/First.cs", graph.Search(["Login", "Find"]).Results[0].File);
    }

    [TestMethod]
    public async Task PersistenceAndCapture_PreserveMetadataAndSelectedResults()
    {
        var graph = await FixtureGraphAsync();
        var captured = graph.Capture();
        var restored = new GraphStore();
        restored.Deserialize(graph.Serialize());
        Assert.IsTrue(restored.HasSearchMetadata);
        Assert.AreEqual(JsonSerializer.Serialize(graph.Search(["Login"])), JsonSerializer.Serialize(restored.Search(["Login"])));
        graph.Publish(Methods(1, "Changed"));
        Assert.AreEqual(0, graph.Search(["Login"]).Results.Count);
        Assert.AreEqual(2, captured.Search(["Login"]).Results.Count);
    }

    [TestMethod]
    public void NodeOnlyPathsAndNamespaceOnlyDocuments_AreSearchableAndDeduplicated()
    {
        var method = new GraphNode("N.Target.Login()", "Method", "/test/Target.cs", "Login", "N");
        var source = new GraphNode("N.Source", "Class", "/test/Source.cs", "Source", "N");
        var graph = new GraphStore().Stage([], [new GraphDocumentRecord("p", "/test/Enums.cs", "", 0, 0, [],
            [new GraphEdge(source, method, "CALLS")], ["Metadata"])], Baseline);
        Assert.AreEqual("Metadata", graph.Search(["Enums"]).Results.Single().Namespaces.Single());
        var target = graph.Search(["Target", "Login"]).Results.Single();
        Assert.IsTrue(target.FileMatched);
        Assert.AreEqual("N", target.Namespaces.Single());
        Assert.AreEqual(method.Id, target.Methods.Single().NodeId);
    }
}
