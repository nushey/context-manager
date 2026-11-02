using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphBuilderTests
{
    [TestMethod]
    public async Task ExtractProjectAsync_NamespaceOnlyFixture_RoundTripsOrderedDistinctNamespaces()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Search", LanguageNames.CSharp);
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "GraphFixtures", "SearchNamespaces.cs");
        project = workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "SearchNamespaces.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(path)), VersionStamp.Default)),
            filePath: "/test/SearchNamespaces.cs")).Project;
        var extraction = await new GraphBuilder(new EdgeExtractor()).ExtractProjectAsync(project, "Search", 0, null);
        var document = extraction.Documents.Values.Single()!;
        Assert.AreEqual(0, document.Nodes.Count);
        CollectionAssert.AreEqual(new[] { "SearchFixtures.First", "SearchFixtures.Last" }, document.Namespaces!.ToArray());
        var graph = new GraphStore().Stage([], [document], GraphSearchTests.Baseline);
        var restored = new GraphStore();
        restored.Deserialize(graph.Serialize());
        CollectionAssert.AreEqual(document.Namespaces.ToArray(), restored.Search(["SearchNamespaces"]).Results.Single().Namespaces.ToArray());
    }

    [TestMethod]
    public void IsSourceDocument_ObjWithAltSeparator_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"/obj/foo.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_BinWithPlatformSeparator_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"\Bin\x.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_GeneratedCs_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"Foo.g.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_CleanSourcePath_Accepted()
    {
        Assert.IsTrue(GraphBuilder.IsSourceDocument(@"Src/Foo.cs"));
    }

    [TestMethod]
    public async Task ExtractProjectAsync_HarvestsConstructorPositionalPropertyAndEntryPointNodes()
    {
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "EdgeCoverage", "EdgeCoverage", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication),
            metadataReferences: references));
        foreach (var name in new[] { "EdgeCoverageCases.cs", "TopLevelProgram.cs" })
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "GraphFixtures", name);
            var document = DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id), name,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(path)), VersionStamp.Default)),
                filePath: Path.Combine("/src", name));
            project = workspace.AddDocument(document).Project;
        }

        var extraction = await new GraphBuilder(new EdgeExtractor()).ExtractProjectAsync(project, "EdgeCoverage", 0, null);

        var nodeIds = extraction.Documents.Values.SelectMany(d => d!.Nodes).Select(n => n.Id).ToList();
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "EdgeCoverageFixtures.ProductResponse.Id",
                "EdgeCoverageFixtures.ProductResponse.Price",
                "EdgeCoverageFixtures.ProductResponse.ProductResponse(int, decimal)",
                "EdgeCoverageFixtures.StandardStrategy.StandardStrategy()",
                "Program",
                "Program.<Main>$(string[])"
            },
            nodeIds,
            string.Join(Environment.NewLine, nodeIds));
    }

    [TestMethod]
    public void GetGraphPath_SolutionsInSameDirectory_UseSeparateFiles()
    {
        Assert.AreNotEqual(GraphRegistry.GetGraphPath("/repo/App.sln"), GraphRegistry.GetGraphPath("/repo/App.Tests.sln"));
        Assert.AreEqual(Path.GetFullPath("/repo/.context-manager/App.graph.json"), GraphRegistry.GetGraphPath("/repo/App.sln"));
    }
}
