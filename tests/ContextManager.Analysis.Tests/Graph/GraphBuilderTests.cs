using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphBuilderTests
{
    // IsSourceDocument normalizes separators before the obj/bin check, so both `/` and `\`
    // forms are rejected. `.g.cs` filtering is separator-independent.

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
}
