using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class InspectContextToolTests
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
}
