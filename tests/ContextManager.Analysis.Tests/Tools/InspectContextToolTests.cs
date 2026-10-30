using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class InspectContextToolTests : FixtureTest
{
    private static InspectContextTool CreateTool() =>
        new(new ContextAnalyzer(new CrossReferenceResolver()));

    [TestMethod]
    public async Task InspectContextAsync_TooManyFiles_ReturnsTooManyFilesError()
    {
        var tool = CreateTool();
        var paths = Enumerable.Range(1, 16)
            .Select(i => $"/nonexistent/path/file{i}.cs")
            .ToList();

        var json = await tool.InspectContextAsync(paths);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("too_many_files", error!.Code);
    }

    [TestMethod]
    public async Task InspectContextAsync_MissingFile_ReturnsFileNotFoundError()
    {
        var tool = CreateTool();
        var missingPath = "/nonexistent/path/does_not_exist.cs";

        var json = await tool.InspectContextAsync([missingPath]);

        var error = JsonSerializer.Deserialize<AnalysisError>(json, AnalysisJson.Options);
        Assert.IsNotNull(error);
        Assert.AreEqual("file_not_found", error!.Code);
        Assert.AreEqual(missingPath, error.FilePath);
    }

    [TestMethod]
    public async Task InspectContextAsync_UnresolvedTypesInGraph_ReturnsEveryMatchAsHint()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "App.sln"), string.Empty);
        var source = Path.Combine(directory, "Controller.cs");
        File.WriteAllText(source, "namespace App;\n\npublic class Controller(IRepository repository, Missing missing) { }\n");
        var store = new GraphStore();
        store.AddNode(new GraphNode("App.Data.IRepository", "Interface", "/src/Data/IRepository.cs"));
        store.AddNode(new GraphNode("App.Legacy.IRepository", "Interface", "/src/Legacy/IRepository.cs"));
        var graphPath = GraphRegistry.GetGraphPath(Path.Combine(directory, "App.sln"));
        Directory.CreateDirectory(Path.GetDirectoryName(graphPath)!);
        File.WriteAllText(graphPath, store.Serialize());
        var tool = new InspectContextTool(new ContextAnalyzer(new CrossReferenceResolver()), new GraphEnricher(new GraphRegistry()));

        var result = JsonSerializer.Deserialize<ContextAnalysis>(await tool.InspectContextAsync([source]), AnalysisJson.Options)!;

        CollectionAssert.AreEquivalent(new[] { "IRepository", "Missing" }, result.Unresolved.ToList());
        CollectionAssert.AreEqual(
            new[] { ("IRepository", "App.Data.IRepository", "/src/Data/IRepository.cs"), ("IRepository", "App.Legacy.IRepository", "/src/Legacy/IRepository.cs") },
            result.UnresolvedHints!.Select(h => (h.Type, h.Id, h.File)).ToList());
    }

    [TestMethod]
    public async Task InspectContextAsync_NoGraph_OmitsHints()
    {
        var directory = NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "App.sln"), string.Empty);
        var source = Path.Combine(directory, "Controller.cs");
        File.WriteAllText(source, "namespace App;\n\npublic class Controller(IRepository repository) { }\n");
        var tool = new InspectContextTool(new ContextAnalyzer(new CrossReferenceResolver()), new GraphEnricher(new GraphRegistry()));

        var json = await tool.InspectContextAsync([source]);

        Assert.IsFalse(json.Contains("unresolvedHints", StringComparison.Ordinal), json);
    }
}
